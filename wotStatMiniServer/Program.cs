using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Dokan;
using System.Configuration;
using System.Xml;

namespace wotStatMiniServer
{
    class StatServer : DokanOperations {
        private struct Member {
            public string StatString;
            public bool HttpError;
            public DateTime ErrorTime;

            public Member(string stat) {
                StatString = stat;
                HttpError = false;
                ErrorTime = DateTime.MinValue;
            }
        }
        public struct Settings {
            public int Timeout;
            public int LogLevel;
            public string DriveLetter;
        }
        private Dictionary<string, Member> cache = new Dictionary<string, Member>();
        private List<string> pendingMembers = new List<string>();
        private string proxyUrl;
        private bool _firstError,
            _unavailable;
        private DateTime _unavailableFrom;
        private Settings _settings;

        public StatServer(Settings settings) {
            _settings = settings;
            Log(2, string.Format("LogLevel: {0}\r\nTimeout: {1}", _settings.LogLevel, _settings.Timeout));
        }

        #region service functions

        private static Settings LoadSettings() {
            Settings result;
            try {
                result.LogLevel = Convert.ToInt32(ConfigurationSettings.AppSettings["logLevel"]);
                result.Timeout = Convert.ToInt32(ConfigurationSettings.AppSettings["timeout"]);
                result.DriveLetter = ConfigurationSettings.AppSettings["driveLetter"];
            } catch {
                result.LogLevel = 1;
                result.Timeout = 1000;
                result.DriveLetter = "n";
            }
            return result;
        }

        private void Log(int level, string message) {
            if(level >= _settings.LogLevel) {
                Console.WriteLine(message);
            }
        }

        private string GetProxyUrl() {
            Random rnd = new Random(DateTime.Now.Millisecond);
            string url = string.Format("stat-proxy-{0}.wot.bkon.ru", rnd.Next(1, 10));
            Log(0, string.Format("SET PROXY: {0}", url));
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
                Log(2, string.Format("Unavailable since {0}", _unavailableFrom));
            } else {
                _firstError = true;
                Log(2, string.Format("First error {0}", DateTime.Now));
            }
        }

        #endregion

        private string GetMembersStatBatch(List<string> members) {
            GetCachedMembersBacth(members);
            return BuildMembersXml(members);
        }

        private string BuildMembersXml(List<string> members) {
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><users>";

            for (var i = 0; i < members.Count; i++)
                xml += cache[members[i]].StatString;
            xml += "</users>";
            return xml;
        }

        private void GetCachedMembersBacth(List<string> members) {
            List<string> forUpdate = new List<string>();

            for(var i = 0;i < members.Count;i++) { 
                var cur = members[i];
                if(cache.ContainsKey(cur)) {
                    Member currentMember = cache[cur];
                    if(!currentMember.HttpError) {
                        Log(1, string.Format("CACHE - {0}", cur));
                        continue;
                    }
                    if(DateTime.Now.Subtract(currentMember.ErrorTime).Minutes < 5)
                        continue;
                    cache.Remove(cur);
                    forUpdate.Add(cur);
                } else {
                    forUpdate.Add(cur);
                }
            }

            // TODO check work with new batch requests
            if(forUpdate.Count == 0 || ServiceUnavailable())
                return;

            try {
                var reqMembers = forUpdate.ToArray();
                string url = string.Format("http://proxy.bulychev.net/polzamer-mod/1/2/{0}", string.Join(",", reqMembers));

                WebRequest request = WebRequest.Create(url);
                request.Credentials = CredentialCache.DefaultCredentials;
                request.Timeout = _settings.Timeout;
                HttpWebResponse response = (HttpWebResponse) request.GetResponse();
                Log(1, string.Format("HTTP - {0}", reqMembers));
                Stream dataStream = response.GetResponseStream();
                StreamReader reader = new StreamReader(dataStream);
                string responseFromServer = reader.ReadToEnd();
                reader.Close();
                dataStream.Close();
                response.Close();

                var resMembers = responseFromServer.Split(',');
                for(var i = 0; i < forUpdate.Count; i++)
                    cache.Add(forUpdate[i], new Member(ResponseToXml(forUpdate[i], resMembers[i])));

            } catch(Exception e) {
                Log(1, string.Format("Exception: {0}", e.Message));
                ErrorHandle();
                for(var i = 0;i < forUpdate.Count;i++) {
                    Member newMember = new Member(string.Format("<user nick=\"{0}\" battles=\"1\" wins=\"0\"></user>", forUpdate[i]));
                    newMember.HttpError = true;
                    newMember.ErrorTime = DateTime.Now;
                    cache.Add(forUpdate[i], newMember);
                }
            }
        }

        private string ResponseToXml(string nick, string response) {
            var percent = response.Substring(response.IndexOf('-') + 1);
            return string.Format("<user nick=\"{0}\" battles=\"{1}\" wins=\"{2}\"></user>", nick, "100", percent);
        }

        /*
     *  "@LOG <string>" - логгирование строки (как бы замена trace()). Можно не реализовывать.
        "@SET_USERS player1[,player2,...]" - Установка текущего списка игроков (пробел после команды).
        "@ADD_USERS player1[,player2,...]" - Добавление игроков к текущему списку (пробел после команды)
        "@RUN" - запуск запроса на получение статистики
        "@GET_USERS" - получить текущий список игроков (будет использоваться в OTM)
        "@GET_LAST_STAT" - получить последнюю полученную статистику
     * */

        public int ReadFile(String filename, Byte[] buffer, ref uint readBytes, long offset, DokanFileInfo info) {
            if(Path.GetFileName(filename)[0] != '@')
                return -1;

            var command = Path.GetFileName(filename);
            var parameters = "";

            try {
                if (command.Contains(" ")) {
                    var spacePos = command.IndexOf(' ');
                    parameters = command.Substring(spacePos + 1);
                    command = command.Substring(0, spacePos);
                }

                switch (command) {
                    case "@LOG":
                        Log(1, parameters);
                        break;

                    case "@SET_USERS":
                        var users = parameters.Split(',');
                        pendingMembers.Clear();
                        pendingMembers.AddRange(users);
                        break;

                    case "@ADD_USERS":
                        pendingMembers.AddRange(parameters.Split(','));
                        break;

                    case "@RUN":
                        GetCachedMembersBacth(pendingMembers);
                        break;
                        
                    case "@GET_USERS":
                    case "@GET_LAST_STAT":
                        string xml = GetMembersStatBatch(pendingMembers);
                        byte[] startSymbols = { 0xEF, 0xBB, 0xBF };
                        byte[] response = Encoding.GetEncoding("iso-8859-1").GetBytes(xml);

                        var result = new byte[startSymbols.Length + response.Length];
                        startSymbols.CopyTo(result, 0);
                        response.CopyTo(result, startSymbols.Length);
                        MemoryStream ms = new MemoryStream(result);
                        ms.Seek(offset, SeekOrigin.Begin);
                        readBytes = (uint)ms.Read(buffer, 0, buffer.Length);
                        break;
                }

                _firstError = false;
                return 0;
            } catch(Exception) {
                return -1;
            }
        }

        public int GetFileInformation(String filename, FileInformation fileinfo, DokanFileInfo info) {
            try {
                int fileLength = 0;
                if(Path.GetFileName(filename)[0] == '@') {
                    var command = Path.GetFileName(filename);
                    var parameters = "";
                    
                    if (command.Contains(" ")) {
                        var spacePos = command.IndexOf(' ');
                        parameters = command.Substring(spacePos + 1);
                        command = command.Substring(0, spacePos);
                        if(command == "@GET_LAST_STAT") {
                            string xml = GetMembersStatBatch(pendingMembers);

                            fileinfo.Length = xml.Length + 3;
                        }
                    }
                }
                fileinfo.Attributes = FileAttributes.Archive;
                fileinfo.CreationTime = DateTime.Now;
                fileinfo.LastAccessTime = DateTime.Now;
                fileinfo.LastWriteTime = DateTime.Now;
                fileinfo.Length = fileLength;
                return 0;
            } catch {
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

        #endregion

        private static void Main(string[] args) {
            Settings settings = LoadSettings();
            string mountPoint = string.Format(@"{0}:\", settings.DriveLetter);

            DokanOptions opt = new DokanOptions();
            opt.DebugMode = true;
            opt.MountPoint = mountPoint;
            opt.ThreadCount = 5;
            Console.WriteLine("Now you can create symlink to drive {0} using\r\nmklink /D c:\\games\\World_of_Tanks\\res\\gui\\flash\\stat {0}", mountPoint);
            int status = DokanNet.DokanMain(opt, new StatServer(settings));
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
