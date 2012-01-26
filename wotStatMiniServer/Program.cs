using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Dokan;

namespace wotStatMiniServer
{
    class StatServer : DokanOperations {
        private struct member {
            public string statString;
            public bool httpError;
            public DateTime errorTime;

            public member(string stat) {
                this.statString = stat;
                this.httpError = false;
                this.errorTime = DateTime.MinValue;
            }
        }
        Dictionary<string, member> cache = new Dictionary<string, member>();
        private string proxyUrl;
        private bool _firstError,
            _unavailable;
        private DateTime _unavailableFrom;

        StatServer() {
            
        }

        private static string GetProxyUrl() {
            Random rnd = new Random(DateTime.Now.Millisecond);
            string url = string.Format("stat-proxy-{0}.wot.bkon.ru", rnd.Next(1, 10));
            Console.WriteLine("SET PROXY: {0}", url);
            return url;
        }

        private bool ServiceUnavailable() {
            if(_unavailable)
                if(DateTime.Now.Subtract(_unavailableFrom).Minutes < 5) {
                    return true;
                } else {
                    _unavailable = false;
                }
            return false;
        }

        private void ErrorHandle() {
            if(_firstError) {
                _unavailable = true;
                _firstError = false;
                _unavailableFrom = DateTime.Now;
                Console.WriteLine("Unavailable since {0}", _unavailableFrom);
            } else {
                _firstError = true;
                Console.WriteLine("First error {0}", DateTime.Now);
            }
        }

        private member GetMemberStat(string member) {
            GetCachedMember(member);

            return cache[member];
        }

        private void GetCachedMember(string member) {
            if(cache.ContainsKey(member))
                return;

            proxyUrl = GetProxyUrl();
            string url = string.Format("http://{0}/{1}.{2}", proxyUrl, member.ToUpper(), "xml");

            WebRequest request = WebRequest.Create(url);
            request.Credentials = CredentialCache.DefaultCredentials;
            request.Timeout = 1000;
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            Console.WriteLine("HTTP status: {0}", response.StatusCode);
            Stream dataStream = response.GetResponseStream();
            StreamReader reader = new StreamReader(dataStream);
            string responseFromServer = reader.ReadToEnd();
            reader.Close();
            dataStream.Close();
            response.Close();

            cache.Add(member, new member(responseFromServer));
        }

        public int ReadFile(String filename, Byte[] buffer, ref uint readBytes, long offset, DokanFileInfo info) {
            if (Path.GetExtension(filename).ToLower() != ".xml")
                return -1;
            try {
                if(ServiceUnavailable())
                    return -1;

                Console.Error.WriteLine("{0} - ReadFile : {1}", DateTime.Now, filename);
                
                string member = Path.GetFileNameWithoutExtension(filename);

                string responseText = GetMemberStat(member).statString;
                byte[] startSymbols = { 0xEF, 0xBB, 0xBF };
                byte[] response = Encoding.GetEncoding("iso-8859-1").GetBytes(responseText);
                var result = new byte[startSymbols.Length + response.Length];
                startSymbols.CopyTo(result, 0);
                response.CopyTo(result, startSymbols.Length);
                MemoryStream ms = new MemoryStream(result);
                ms.Seek(offset, SeekOrigin.Begin);
                readBytes = (uint)ms.Read(buffer, 0, buffer.Length);
                _firstError = false;
                return 0;
            } catch(Exception) {
                ErrorHandle();
                return -1;
            }
        }

        public int GetFileInformation(String filename, FileInformation fileinfo, DokanFileInfo info) {
            try {
                if(ServiceUnavailable())
                    return -1;

                int fileLength = 0;
                if(Path.GetExtension(filename).ToLower() == ".xml") {
                    string member = Path.GetFileNameWithoutExtension(filename);
                    fileLength = GetMemberStat(member).statString.Length + 3;
                }
                fileinfo.Attributes = FileAttributes.Archive;
                fileinfo.CreationTime = DateTime.Now;
                fileinfo.LastAccessTime = DateTime.Now;
                fileinfo.LastWriteTime = DateTime.Now;
                fileinfo.Length = fileLength;
                return 0;
            } catch {
                ErrorHandle();
                return -1;
            }
        }

        #region default implementations

        public int CreateFile(String filename, FileAccess access, FileShare share,
                              FileMode mode, FileOptions options, DokanFileInfo info)
        {
            return 0;
        }

        public int OpenDirectory(String filename, DokanFileInfo info)
        {
            return 0;
        }

        public int CreateDirectory(String filename, DokanFileInfo info)
        {
            return -1;
        }

        public int Cleanup(String filename, DokanFileInfo info)
        {
            return 0;
        }

        public int WriteFile(String filename, Byte[] buffer,
                             ref uint writtenBytes, long offset, DokanFileInfo info)
        {
            return -1;
        }

        public int FlushFileBuffers(String filename, DokanFileInfo info)
        {
            return -1;
        }

        public int CloseFile(String filename, DokanFileInfo info)
        {
            return 0;
        }

        public int FindFiles(String filename, ArrayList files, DokanFileInfo info) {
            return 0;
        }

        public int SetFileAttributes(String filename, FileAttributes attr, DokanFileInfo info) {
            return -1;
        }

        public int SetFileTime(String filename, DateTime ctime,
                               DateTime atime, DateTime mtime, DokanFileInfo info) {
            return -1;
        }

        public int DeleteFile(String filename, DokanFileInfo info) {
            return -1;
        }

        public int DeleteDirectory(String filename, DokanFileInfo info) {
            return -1;
        }

        public int MoveFile(String filename, String newname, bool replace, DokanFileInfo info) {
            return -1;
        }

        public int SetEndOfFile(String filename, long length, DokanFileInfo info) {
            return -1;
        }

        public int SetAllocationSize(String filename, long length, DokanFileInfo info) {
            return -1;
        }

        public int LockFile(String filename, long offset, long length, DokanFileInfo info) {
            return 0;
        }

        public int UnlockFile(String filename, long offset, long length, DokanFileInfo info) {
            return 0;
        }

        #endregion

        public int GetDiskFreeSpace(ref ulong freeBytesAvailable, ref ulong totalBytes,
                                    ref ulong totalFreeBytes, DokanFileInfo info) {
            freeBytesAvailable = 512*1024*1024;
            totalBytes = 1024*1024*1024;
            totalFreeBytes = 512*1024*1024;
            return 0;
        }

        public int Unmount(DokanFileInfo info) {
            return 0;
        }

        private static void Main(string[] args) {
            DokanOptions opt = new DokanOptions();
            opt.DebugMode = true;
            string mountPoint = args.Length > 0 ? string.Format("{0}\\", args[0]) : "n:\\";
            opt.MountPoint = mountPoint;
            opt.ThreadCount = 5;
            Console.WriteLine("Now you can create symlink to drive {0} using\r\nmklink /D c:\\games\\World_of_Tanks\\res\\gui\\flash\\stat {0}", mountPoint);
            int status = DokanNet.DokanMain(opt, new StatServer());
            switch (status) {
                case DokanNet.DOKAN_DRIVE_LETTER_ERROR:
                    Console.WriteLine("Drvie letter error");
                    break;
                case DokanNet.DOKAN_DRIVER_INSTALL_ERROR:
                    Console.WriteLine("Driver install error");
                    break;
                case DokanNet.DOKAN_MOUNT_ERROR:
                    Console.WriteLine("Mount error");
                    break;
                case DokanNet.DOKAN_START_ERROR:
                    Console.WriteLine("Start error");
                    break;
                case DokanNet.DOKAN_ERROR:
                    Console.WriteLine("Unknown error");
                    break;
                case DokanNet.DOKAN_SUCCESS:
                    Console.WriteLine("Success");
                    break;
                default:
                    Console.WriteLine("Unknown status: {0}", status);
                    break;
            }
        }
    }
}
