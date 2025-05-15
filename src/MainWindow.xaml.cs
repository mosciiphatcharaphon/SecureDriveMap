using KS2Drive.Config;
using KS2Drive.Log;
using KS2Drive.WinFSP;
using MahApps.Metro.Controls;
using NLog.Filters;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Serialization;
using static KS2Drive.FSPService;

namespace KS2Drive
{
    public partial class MainWindow : MetroWindow
    {
        private FSPService Service;
        private bool IsMounted = false;
        private Thread T;
        LogListItem L;
        private Configuration AppConfiguration;
        public ObservableCollection<LogListItem> ItemsToLog = new ObservableCollection<LogListItem>();
        public List<string> driveLetterList = new List<string>();

        //Set the WinFSP version against which the program was built (see /Reference folder in source)
        //Installer ProductCode (can be extracted from MSI file with superorca http://www.pantaray.com/msi_super_orca.html)
        //Installer URL
        //WinFSP Version Name
        private (String MsiProductCode, String PackageURL, String VersionName) RequiredWinFSP = ("{4EE1629A-41FB-4261-847A-C12B466B017D}", "https://github.com/billziss-gh/winfsp/releases/download/v1.5/winfsp-1.5.20002.msi", "WinFsp 2019.3");
        private System.Windows.Forms.NotifyIcon AppNotificationIcon;
        private ContextMenu AppMenu;

        public MainWindow()
        {
            InitializeComponent();

            AppConfiguration = ((App)Application.Current).AppConfiguration;

            AppMenu = (ContextMenu)this.FindResource("NotifierContextMenu");
            ((MenuItem)AppMenu.Items[0]).IsEnabled = AppConfiguration.IsConfigured;

            this.Hide();

            //Check installed WinFSP version
            if (!Tools.IsMsiIntalled(RequiredWinFSP.MsiProductCode))
            {
                WinFSPUI Dialog = new WinFSPUI(RequiredWinFSP);
                Dialog.ShowDialog();
                if (!Dialog.IsInstallSuccessFull)
                {
                    QuitApp();
                    return;
                }
            }

            #region Window events

            this.Closing += (s, e) =>
            {
                e.Cancel = true;
                this.Hide();
            };

            #endregion

            #region Icon

            AppNotificationIcon = new System.Windows.Forms.NotifyIcon();
            Stream iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/SecureDrive;component/Resources/securedrive_logo.ico")).Stream;
            AppNotificationIcon.Icon = new System.Drawing.Icon(iconStream);
            AppNotificationIcon.Visible = true;
            AppNotificationIcon.Text = "SecureDrive";
            AppNotificationIcon.BalloonTipText = "SecureDrive";
            AppNotificationIcon.MouseClick += (s, e) => { this.Dispatcher.Invoke(() => { AppMenu.IsOpen = !AppMenu.IsOpen; }); };

            #endregion

            LogList.ItemsSource = ItemsToLog;

            #region Try to start WinFSP Service

            try
            {
                Service = new FSPService();

                #region Service Events

                Service.RepositoryActionPerformed += (s1, e1) =>
                {
                    Dispatcher.Invoke(() => ItemsToLog.Add(e1));
                    if (!e1.Result.Equals("STATUS_SUCCESS"))
                    {
                        if (!e1.AllowRetryOrRecover) Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"The action {e1.Method} for the file {e1.File} failed", System.Windows.Forms.ToolTipIcon.Warning));
                        else Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"The action {e1.Method} for the file {e1.File} failed. You can recover this file via the LOG menu", System.Windows.Forms.ToolTipIcon.Warning));
                    }
                    ;
                };

                Service.RepositoryAuthenticationFailed += (s2, e2) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"Your credentials are invalid. Please update them in the Configuration panel", System.Windows.Forms.ToolTipIcon.Error);
                    });
                };

                #endregion

                T = new Thread(() => Service.Run());
                T.Start();
            }
            catch
            {
                MessageBox.Show("Cannot start WinFSP service. KS² Drive will now close", "", MessageBoxButton.OK, MessageBoxImage.Error);
                QuitApp();
                return;
            }

            #endregion

            Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"KS² Drive has started", System.Windows.Forms.ToolTipIcon.Info));

            if (this.AppConfiguration.IsConfigured)
            {
                if (AppConfiguration.AutoMount) MountDrive();
            }
            else
            {
                MenuConfigure_Click(this, null);
            }
        }
        private async void MountDrive()
        {
            OcsResponse res = await GetGroupFolderXml(this.AppConfiguration);
            if (res?.Data?.Elements == null)
            {
                ItemsToLog.Add(new LogListItem()
                {
                    Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Object = "null",
                    Method = $"MountDrive",
                    File = this.AppConfiguration.ServerURL,
                    Result = "Error Username or Password is incorrect "
                });
                LogList.ItemsSource = ItemsToLog;
                return;
            }
            string driveLetter = this.AppConfiguration.DriveLetter;
            foreach (var folder in res.Data.Elements)
            {
                Configuration newConfig = CloneConfiguration(this.AppConfiguration);
                newConfig.Permission = GetFolderPermission(folder);
                if (folder.Id != -1 && !string.IsNullOrEmpty(folder.ParentsPath))
                {
                    newConfig.ServerURL = $"{this.AppConfiguration.ServerURL.TrimEnd('/')}/{folder.ParentsPath.TrimStart('/')}/{folder.MountPoint.TrimStart('/')}";
                    newConfig.VolumeLabel = folder.MountPoint;
                }
                else
                {
                    newConfig.ServerURL = this.AppConfiguration.ServerURL;
                    newConfig.VolumeLabel = $"ไดร์ฟของ {this.AppConfiguration.ServerLogin}";
                }
                if (folder.IsDrive != 0 || folder.Id == -1)
                {
                    newConfig.DriveLetter = GetNextDriveLetter(ref driveLetter);
                    newConfig.quota = ulong.Parse(folder.Quota.ToString());
                    try
                    {
                        driveLetterList = await Service.Mount(newConfig);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message);
                        return;
                    }
                    ItemsToLog.Clear();
                    ((MenuItem)AppMenu.Items[0]).Header = "_UNMOUNT";
                    IsMounted = true;
                    ((MenuItem)AppMenu.Items[2]).IsEnabled = false;
                    Process.Start($@"{newConfig.DriveLetter.Replace(":", "")}:\");
                    Thread.Sleep(2500);
                }
            }
        }
        private Configuration CloneConfiguration(Configuration source)
        {
            return new Configuration
            {
                IsConfigured = source.IsConfigured,
                AutoMount = source.AutoMount,
                DriveLetter = source.DriveLetter,
                ServerType = source.ServerType,
                ServerLogin = source.ServerLogin,
                ServerPassword = source.ServerPassword,
                KernelCacheMode = source.KernelCacheMode,
                FlushMode = source.FlushMode,
                SyncOps = source.SyncOps,
                PreLoading = source.PreLoading,
                MountAsNetworkDrive = source.MountAsNetworkDrive,
                HTTPProxyMode = source.HTTPProxyMode,
                ProxyURL = source.ProxyURL,
                UseProxyAuthentication = source.UseProxyAuthentication,
                ProxyLogin = source.ProxyLogin,
                ProxyPassword = source.ProxyPassword,
                UseClientCertForAuthentication = source.UseClientCertForAuthentication,
                CertStoreName = source.CertStoreName,
                CertStoreLocation = source.CertStoreLocation,
                CertSerial = source.CertSerial
            };
        }
        private int GetFolderPermission(GroupElements folder)
        {
            if (folder.Groups?.AnyElements == null) return 31;

            foreach (var group in folder.Groups.AnyElements)
            {
                if (group.Name == "admin" && int.TryParse(group.InnerText, out int permission))
                {
                    return permission;
                }
            }

            return 0;
        }
        private string GetNextDriveLetter(ref string currentLetter)
        {
            var usedDriveLetters = new HashSet<char>(
                DriveInfo.GetDrives()
                         .Select(d => char.ToUpper(d.Name[0]))
            );
            char letter = string.IsNullOrEmpty(currentLetter)
                ? char.ToUpper(this.AppConfiguration.DriveLetter[0])
                : char.ToUpper(currentLetter[0]);
            while (letter <= 'Z')
            {
                if (!usedDriveLetters.Contains(letter))
                {
                    currentLetter = $"{letter}:";
                    return currentLetter.Replace(":","");
                }

                letter++;
            }
            throw new InvalidOperationException("No available drive letters from A to Z.");
        }
        //private async void MountDrive()
        //{
        //    OcsResponse res = await GetGroupFolderXml(this.AppConfiguration);
        //    if (res?.Data?.Elements == null) 
        //        return;
        //    string driveLetter = "";
        //    foreach (var folder in res.Data.Elements)
        //    {
        //        var newConfig = new Configuration();
        //        newConfig.IsConfigured = this.AppConfiguration.IsConfigured;
        //        newConfig.AutoMount = this.AppConfiguration.AutoMount;
        //        newConfig.DriveLetter = this.AppConfiguration.DriveLetter;
        //        newConfig.ServerType = this.AppConfiguration.ServerType;
        //        newConfig.ServerLogin = this.AppConfiguration.ServerLogin;
        //        newConfig.ServerPassword = this.AppConfiguration.ServerPassword;
        //        newConfig.KernelCacheMode = this.AppConfiguration.KernelCacheMode;
        //        newConfig.FlushMode = this.AppConfiguration.FlushMode;
        //        newConfig.SyncOps = this.AppConfiguration.SyncOps;
        //        newConfig.PreLoading = this.AppConfiguration.PreLoading;
        //        newConfig.MountAsNetworkDrive = this.AppConfiguration.MountAsNetworkDrive;
        //        newConfig.HTTPProxyMode = this.AppConfiguration.HTTPProxyMode;
        //        newConfig.ProxyURL = this.AppConfiguration.ProxyURL;
        //        newConfig.UseProxyAuthentication = this.AppConfiguration.UseProxyAuthentication;
        //        newConfig.ProxyLogin = this.AppConfiguration.ProxyLogin;
        //        newConfig.ProxyPassword = this.AppConfiguration.ProxyPassword;
        //        newConfig.UseClientCertForAuthentication = this.AppConfiguration.UseClientCertForAuthentication;
        //        newConfig.CertStoreName = this.AppConfiguration.CertStoreName;
        //        newConfig.CertStoreLocation = this.AppConfiguration.CertStoreLocation;
        //        newConfig.CertSerial = this.AppConfiguration.CertSerial;
        //        int permission = 0;
        //        if (folder.Groups.AnyElements != null) 
        //        {
        //            foreach (var group in folder.Groups.AnyElements)
        //            {
        //                if (group.Name == "admin") 
        //                {
        //                    permission = int.Parse(group.InnerText);
        //                }
        //            }
        //        }
        //        newConfig.Permission = permission;
        //        if (folder.Id != -1)
        //        {
        //            if (!string.IsNullOrEmpty(folder.ParentsPath))
        //            {
        //                newConfig.ServerURL = $"{this.AppConfiguration.ServerURL.TrimEnd('/')}/{folder.ParentsPath.TrimStart('/')}/{folder.MountPoint.TrimStart('/')}";
        //            }
        //        }
        //        if (string.IsNullOrEmpty(newConfig.ServerURL))
        //        {
        //            newConfig.ServerURL = this.AppConfiguration.ServerURL;
        //            newConfig.DriveLetter = this.AppConfiguration.DriveLetter;
        //        }
        //        if (folder.IsDrive != 0 || folder.Id == -1)
        //        {
        //            if (string.IsNullOrEmpty(driveLetter))
        //            {
        //                driveLetter = this.AppConfiguration.DriveLetter;
        //            }
        //            else
        //            {
        //                driveLetter = driveLetterList[driveLetterList.Count - 1];
        //                driveLetter = ((char)(driveLetter[0] + 1)).ToString();
        //                newConfig.DriveLetter = driveLetter;
        //            }
        //            try
        //            {
        //                driveLetterList = await Service.Mount(newConfig);
        //            }
        //            catch (Exception ex)
        //            {
        //                MessageBox.Show(ex.Message);
        //                return;
        //            }
        //            ItemsToLog.Clear();
        //            ((MenuItem)AppMenu.Items[0]).Header = "_UNMOUNT";
        //            IsMounted = true;
        //            ((MenuItem)AppMenu.Items[2]).IsEnabled = false;
        //            Process.Start($@"{driveLetter.Replace(":", "")}:\");
        //            Thread.Sleep(2500);
        //        }

        //    }
        //}
        private async Task<OcsResponse> GetGroupFolderXml(Configuration config)
        {
            try
            {
                var uri = new Uri(config.ServerURL);
                var baseUrl = $"{uri.Scheme}://{uri.Host}";
                if (!uri.IsDefaultPort)
                {
                    baseUrl += $":{uri.Port}";
                }
                string username = config.ServerLogin;
                string password = config.ServerPassword;
                string url = $"{baseUrl}/ocs/v2.php/cloud/users/{username}/groupFolder";

                using (var client = new HttpClient())
                {
                    var byteArray = Encoding.ASCII.GetBytes($"{username}:{password}");
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                    client.DefaultRequestHeaders.Add("OCS-APIRequest", "true");

                    var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    var xml = await response.Content.ReadAsStringAsync();
                    var serializer = new XmlSerializer(typeof(OcsResponse));
                    using (var reader = new StringReader(xml))
                    {
                        return (OcsResponse)serializer.Deserialize(reader);
                    }
                }
            }
            catch (Exception ex)
            { }
            return null;
        }
        private void UnmountDrive()
        {
            try
            {
                Service.Unmount();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

            ((MenuItem)AppMenu.Items[0]).Header = "_MOUNT";
            IsMounted = false;
            ((MenuItem)AppMenu.Items[2]).IsEnabled = true;
            ((MenuItem)AppMenu.Items[2]).ToolTip = null;
        }

        /// <summary>
        /// Hide window when minimized
        /// </summary>
        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == System.Windows.WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        /// <summary>
        /// Cleanup and shutdown the app
        /// </summary>
        private void QuitApp()
        {
            if (AppNotificationIcon != null)
            {
                AppNotificationIcon.Visible = false;
                AppNotificationIcon.Dispose();
            }
            Service?.Stop();
            Application.Current.Shutdown();
        }

        #region Menu actions

        private void MenuMount_Click(object sender, RoutedEventArgs e)
        {
            if (IsMounted) UnmountDrive();
            else MountDrive();
        }

        private void MenuConfigure_Click(object sender, RoutedEventArgs e)
        {
            ConfigurationUI OptionWindow = new ConfigurationUI();
            OptionWindow.ShowDialog();
            if (AppConfiguration.IsConfigured) ((MenuItem)AppMenu.Items[0]).IsEnabled = true;
        }

        private void MenuLog_Click(object sender, RoutedEventArgs e)
        {
            if (!this.IsVisible) this.Show();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            QuitApp();
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            About.About A = new About.About();
            A.ShowDialog();
        }

        #endregion

        #region Log actions

        private void bt_ClearLog_Click(object sender, RoutedEventArgs e)
        {
            ItemsToLog.Clear();
        }

        private void bt_ExportLog_Click(object sender, RoutedEventArgs e)
        {
            StringBuilder SB = new StringBuilder();
            foreach (var I in ItemsToLog)
            {
                SB.AppendLine($"{I.Date};{I.Object};{I.Method};{I.File};{I.Result};");
            }

            System.Windows.Forms.SaveFileDialog SFD = new System.Windows.Forms.SaveFileDialog();
            SFD.Filter = "CSV File|*.csv";
            SFD.Title = "Save a CSV File";
            SFD.FileName = "LogExport.csv";
            SFD.ShowDialog();

            if (!String.IsNullOrEmpty(SFD.FileName))
            {
                try
                {
                    File.WriteAllText(SFD.FileName, SB.ToString());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void bt_FileRecover_Click(object sender, RoutedEventArgs e)
        {
            var SenderButton = (Button)sender;
            var FileInfo = (LogListItem)SenderButton.Tag;

            System.Windows.Forms.SaveFileDialog saveFileDialog = new System.Windows.Forms.SaveFileDialog();
            saveFileDialog.FileName = Path.GetFileName(FileInfo.File);
            if (saveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    File.Copy(FileInfo.LocalTemporaryPath, saveFileDialog.FileName);
                    MessageBox.Show("File has been saved");
                }
                catch
                {
                    MessageBox.Show("Failed to save file");
                }
            }
        }
        #endregion

    }
}