using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using WebDAVClient.Helpers;

namespace KS2Drive.FS
{
    public class WebDavClient2 : WebDAVClient.Client
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static List<string> _RootPaths = new List<string>();
        private static String _Server;
        private static String _Login;
        private static String _Password;
        private static bool _IsInited = false;
        private static X509Certificate2 _ClientCert;

        public static void Init(String Server, String BasePath, String Login, String Password, X509Certificate2 ClientCert)
        {
            if (!_RootPaths.Contains(BasePath))
            {
                _RootPaths.Add(BasePath);
            }
            WebDavClient2._Server = Server;
            WebDavClient2._Login = Login;
            WebDavClient2._Password = Password;
            WebDavClient2._IsInited = true;
            WebDavClient2._ClientCert = ClientCert;
        }

        public WebDavClient2(TimeSpan? uploadTimeout = null) :
        base(new NetworkCredential { UserName = _Login, Password = _Password }, uploadTimeout, null, _ClientCert)
        {
            if (!_IsInited) throw new InvalidOperationException("Please Call Init First");
            base.Server = _Server;
            foreach (var path in _RootPaths)
            {
                base.BasePath = path;
                try
                {
                    var test = this.List(path).GetAwaiter().GetResult();
                    if (test != null)
                    {
                        logger.Info($"Using WebDAV path: {path}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"WebDAV path not available: {path}");
                }
            }

            throw new InvalidOperationException("No usable WebDAV root paths found.");
        }

        public string ParameterConvert(string input)
    {
        foreach (var rootPath in _RootPaths)
        {
            if (input.StartsWith(rootPath))
                return input.Substring(rootPath.Length);
        }
        return input;
    }
        public new Task<bool> CreateDir(string remotePath, string name)
        {
            remotePath = ParameterConvert(remotePath);
            return base.CreateDir(remotePath, name);
        }

        public new Task DeleteFile(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.DeleteFile(remotePath);
        }

        public new Task DeleteFolder(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.DeleteFolder(remotePath);
        }

        public new Task<Byte[]> Download(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.Download(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFile(string remotePath = "/")
        {
            remotePath = ParameterConvert(remotePath);
            return base.GetFile(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFolder(string remotePath = "/")
        {
            remotePath = ParameterConvert(remotePath);
            return base.GetFolder(remotePath);
        }

        public new Task<IEnumerable<WebDAVClient.Model.Item>> List(string remotePath = "/", int? depth = 1)
        {
            remotePath = ParameterConvert(remotePath);
            return base.List(remotePath, depth);
        }

        public new Task<bool> MoveFile(string srcFilePath, string dstFilePath)
        {
            srcFilePath = ParameterConvert(srcFilePath);
            dstFilePath = ParameterConvert(dstFilePath);
            return base.MoveFile(srcFilePath, dstFilePath);
        }

        public new Task<bool> MoveFolder(string srcFolderPath, string dstFolderPath)
        {
            srcFolderPath = ParameterConvert(srcFolderPath);
            dstFolderPath = ParameterConvert(dstFolderPath);
            return base.MoveFolder(srcFolderPath, dstFolderPath);
        }

        public new Task<bool> Upload(string remoteFilePath, Stream content, string name)
        {
            remoteFilePath = ParameterConvert(remoteFilePath);
            return base.Upload(remoteFilePath, content, name);
        }

        /// <summary>
        /// Retrieve a file or folder from the remote repository
        /// Return either a RepositoryElement or a FileSystem Error Message
        /// </summary>
        public WebDAVClient.Model.Item GetRepositoryElement(String LocalFileName)
        {
            String RepositoryDocumentName = FileNode.ConvertLocalPathToRepositoryPath(LocalFileName);
            WebDAVClient.Model.Item RepositoryElement = null;

            if (RepositoryDocumentName.Contains("."))
            {
                //We assume the FileName refers to a file
                try
                {
                    RepositoryElement = this.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
                    return RepositoryElement;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                {
                    return null;
                }
                catch (WebDAVException ex)
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            else
            {
                //We assume it's a folder
                try
                {
                    RepositoryElement = this.GetFolder(RepositoryDocumentName).GetAwaiter().GetResult();
                    if (FileNode.IsRepositoryRootPath(RepositoryDocumentName)) RepositoryElement.DisplayName = "";
                    return RepositoryElement;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                {
                    //Try as a file
                    try
                    {
                        RepositoryElement = this.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
                        return RepositoryElement;
                    }
                    catch (WebDAVException ex1) when (ex1.GetHttpCode() == 404)
                    {
                        return null;
                    }
                    catch (WebDAVException ex1)
                    {
                        throw ex1;
                    }
                    catch (Exception ex1)
                    {
                        throw ex1;
                    }
                }
                catch (WebDAVException ex)
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }
    }
}
