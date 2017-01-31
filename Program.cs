using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BuildPacker
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            List<string> diffFiles = ReadConsoleInput(3000).ToList();

            if (diffFiles.Count <= 0)
            {
                Console.WriteLine("No input entered!");
                return;
            }

            DateTime versionDate = DateTime.Today;
            int hotfixNumber = 0;

            if (args.Length > 0 && !(Regex.IsMatch(args[0], @"^\d+\.\d+\.\d+\.\d+$") &&
                DateTime.TryParse(Regex.Match(args[0], @"^\d+\.\d+\.\d+").Value, out versionDate) &&
                int.TryParse(Regex.Match(args[0], @"\d+$").Value, out hotfixNumber)))
            {
                Console.WriteLine("Please enter the version number in yyyy.MM.dd.# format.");
                return;
            }

            string versionNumber = string.Format("{0:yyyy.M.d}.{1}", versionDate, hotfixNumber);
            string versionName = string.Format("v{0:yyyy.MM.dd}.{1}", versionDate, hotfixNumber);

            string versionDirPath = Path.GetDirectoryName(Directory.GetCurrentDirectory()) + "\\" + versionName;

            if (Directory.Exists(versionDirPath))
            {
                Console.WriteLine("There already exists a folder with the same name: {0}", versionName);
                Console.WriteLine("Please try again after deleting/renaming the existed one.");
                return;
            }
            Directory.CreateDirectory(versionDirPath);

            List<string> assemblyInfoPaths = new List<string>();
            List<FileStatus> sqls = new List<FileStatus>();

            try
            {
                foreach (string file in diffFiles)
                {
                    if (!file.Contains('/')) continue;

                    FileStatus gitStatus = new FileStatus(file);

                    if (gitStatus.RelativePath.ToLower().StartsWith("dbscript"))
                    {
                        sqls.Add(gitStatus);
                        continue;
                    }

                    string directory = Path.GetDirectoryName(gitStatus.FullPath);

                    while (!string.IsNullOrEmpty(directory) && !File.Exists(directory + @"\Properties\AssemblyInfo.cs"))
                        directory = Path.GetDirectoryName(directory);

                    if (string.IsNullOrEmpty(directory)) continue;

                    string assemblyInfoPath = directory + @"\Properties\AssemblyInfo.cs";

                    if (!assemblyInfoPaths.Contains(assemblyInfoPath))
                        assemblyInfoPaths.Add(assemblyInfoPath);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Directory.Delete(versionDirPath, true);
                return;
            }

            if (sqls.Count == 0)
                Console.WriteLine("No SQL scripts were modified.");
            else
            {
                CheckSqlFormat(sqls);

                string sqlCollectionPath = versionDirPath + @"\SQL";
                CollectSql(sqlCollectionPath, sqls);
                MergeSql(sqlCollectionPath);
                Console.WriteLine("All modified SQL scripts have been copied and merged.");
            }

            if (assemblyInfoPaths.Count == 0)
                Console.WriteLine("No assemblies were modified.");
            else
                StampVersionNumber(assemblyInfoPaths, versionNumber);

            GenerateOutline(versionDirPath, sqls, assemblyInfoPaths);
        }

        /// <summary>
        /// 檢查*.sql檔案的編碼及換行字元，若是Big5或是UTF-8-BOM，則轉碼為UTF-8，若是LF或是CR則轉為CRLF
        /// </summary>
        private static void CheckSqlFormat(IEnumerable<FileStatus> sqls)
        {
            foreach (FileStatus sql in sqls)
            {
                if (!File.Exists(sql.FullPath)) continue;

                byte[] content = File.ReadAllBytes(sql.FullPath);
                if (content.IsBig5Encoding())
                    content = Encoding.Convert(Encoding.GetEncoding("big5"), Encoding.UTF8, content);
                else if (content.IsUtf8EncodingWithBom())
                    content = content.SubArray(3);

                string totalString = Encoding.UTF8.GetString(content);

                // 修正換行字元
                totalString = Regex.Replace(totalString, @"\r\n?|\n", "\r\n");

                // 刪除選項
                if (sql.RelativePath.Split('/')[1] == "Table")
                    totalString = Regex.Replace(totalString, @"INSERT #appTableFieldo .+;\r\n(INSERT #appTableFieldoi .+;\r\n)+(\r\n)*", string.Empty);
                File.WriteAllText(sql.FullPath, totalString);
            }
        }

        /// <summary>
        /// 將異動到的*.sql檔複製並分類到一資料夾裡的子資料夾
        /// </summary>
        private static void CollectSql(string sqlCollectionPath, IEnumerable<FileStatus> sqls)
        {
            Dictionary<string, List<FileStatus>> modifiedFiles = new Dictionary<string, List<FileStatus>>();
            Dictionary<string, List<string>> deletedFiles = new Dictionary<string, List<string>>();

            foreach (FileStatus sql in sqls)
            {
                string type = sql.RelativePath.Split('/')[1];
                if (sql.Status == "D")
                {
                    if (!deletedFiles.ContainsKey(type))
                        deletedFiles.Add(type, new List<string>());
                    deletedFiles[type].Add(Path.GetFileNameWithoutExtension(sql.FileName));
                }
                else
                {
                    if (!File.Exists(sql.FullPath)) continue;

                    if (!modifiedFiles.ContainsKey(type))
                        modifiedFiles.Add(type, new List<FileStatus>());
                    modifiedFiles[type].Add(sql);
                }
            }

            foreach (string type in modifiedFiles.Keys)
            {
                string destinationPath = string.Empty;
                switch (type)
                {
                    case "Table": destinationPath = sqlCollectionPath + @"\1.Table"; break;
                    case "Type": destinationPath = sqlCollectionPath + @"\2.Tp"; break;
                    case "View": destinationPath = sqlCollectionPath + @"\3.Vw"; break;
                    case "Function": destinationPath = sqlCollectionPath + @"\4.Fn"; break;
                    case "StoredProcedure": destinationPath = sqlCollectionPath + @"\5.Sp"; break;
                    default: destinationPath = sqlCollectionPath + @"\6.Other"; break;
                }
                Directory.CreateDirectory(destinationPath);
                modifiedFiles[type].ForEach(sql => File.Copy(sql.FullPath, destinationPath + '\\' + sql.FileName));
            }

            if (deletedFiles.Count > 0)
            {
                Directory.CreateDirectory(sqlCollectionPath + @"\0.Before");
                StringBuilder stringBuilder = new StringBuilder();

                string[] types = { "StoredProcedure", "Function", "View", "Type", "Table" };

                foreach (string type in types)
                {
                    if (deletedFiles.ContainsKey(type))
                    {
                        deletedFiles[type].ForEach(fileName =>
                        {
                            stringBuilder.AppendLine(GetDropScript(type, fileName));
                            stringBuilder.AppendLine();
                        });
                    }
                }

                File.WriteAllText(sqlCollectionPath + @"\0.Before\prefix.sql", stringBuilder.ToString());
            }
        }

        /// <summary>
        /// 產生一件清單，列出所異動到的*.sql以及組件
        /// </summary>
        private static void GenerateOutline(string versionDirPath, IEnumerable<FileStatus> sqls, IEnumerable<string> assemblyInfoPaths)
        {
            if (sqls.Count() > 0 || assemblyInfoPaths.Count() > 0)
                using (StreamWriter sw = new StreamWriter(versionDirPath + @"\ModifiedFiles.txt", false))
                {
                    if (sqls.Count() > 0)
                    {
                        sw.WriteLine("Modified SQL files:");
                        sw.WriteLine();
                        foreach (var sql in sqls)
                            sw.WriteLine(string.Format("{0}   {1}", sql.Status, sql.RelativePath));
                        sw.WriteLine();
                    }
                    if (assemblyInfoPaths.Count() > 0)
                    {
                        sw.WriteLine("Modified assemblies:");
                        sw.WriteLine();
                        foreach (string assemblyInfoPath in assemblyInfoPaths)
                        {
                            string[] pathSegments = assemblyInfoPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                            if (pathSegments.Length >= 3)
                                sw.WriteLine(pathSegments[pathSegments.Length - 3]);
                        }
                        sw.WriteLine();
                    }
                }
            else
                Directory.Delete(versionDirPath, true);
        }

        /// <summary>
        /// 回傳卸除相關物件之SQL Script
        /// </summary>
        /// <param name="type">目前僅支援：StoredProcedure、Function、View、Type、Table</param>
        /// <param name="fileName">該檔案名稱，需與SQL物件同名</param>
        /// <returns></returns>
        private static string GetDropScript(string type, string fileName)
        {
            switch (type)
            {
                case "StoredProcedure":
                    return string.Format(
@"IF EXISTS (SELECT 1 FROM dbo.sysobjects where id = OBJECT_ID(N'[dbo].[{0}]') AND OBJECTPROPERTY(id, N'IsProcedure') = 1)
    DROP PROCEDURE [dbo].[{0}];", fileName);

                case "Function":
                    return string.Format(
@"IF EXISTS (SELECT 1 FROM sysobjects WHERE id = OBJECT_ID(N'[dbo].[{0}]') AND xtype IN ('FN','IF','TF'))
    DROP FUNCTION [dbo].[{0}];", fileName);

                case "View":
                    return string.Format(
@"IF EXISTS (SELECT 1 FROM dbo.sysobjects WHERE id = OBJECT_ID(N'[dbo].[{0}]') AND OBJECTPROPERTY(id, N'IsView') = 1)
    DROP VIEW [dbo].[{0}]", fileName);

                case "Type":
                    return string.Format(
@"IF EXISTS (SELECT 1 FROM sys.types st JOIN sys.schemas ss ON st.schema_id = ss.schema_id WHERE st.name = N'{0}' AND ss.name = N'dbo')
    DROP TYPE [dbo].[{0}]", fileName);

                case "Table":
                    return string.Format(
@"IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[{0}]') AND type in (N'U'))
    DROP TABLE [dbo].[{0}];
DELETE FROM app_table WHERE tablename = '{0}';
DELETE FROM app_table_field WHERE tablename = '{0}';", fileName);

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 偵測bytes[]是否為Big5編碼
        /// </summary>
        private static bool IsBig5Encoding(this byte[] bytes)
        {
            Encoding big5 = Encoding.GetEncoding(950);

            //將byte[]轉為string再轉回byte[]看位元數是否有變
            return bytes.Length == big5.GetByteCount(big5.GetString(bytes));
        }

        /// <summary>
        /// 偵測bytes[]是否為UTF-8-BOM編碼
        /// </summary>
        private static bool IsUtf8EncodingWithBom(this byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        /// <summary>
        /// 合併一資料夾底下所有的sql檔
        /// </summary>
        private static void MergeSql(string sqlCollectionPath)
        {
            string[] sqlPaths = Directory.GetFiles(sqlCollectionPath, "*.sql", SearchOption.AllDirectories);

            using (StreamWriter streamWriter = new StreamWriter(Path.GetDirectoryName(sqlCollectionPath) + @"\MergedSQLScript.sql", false))
            {
                foreach (string sqlPath in sqlPaths)
                {
                    streamWriter.WriteLine("-- " + Path.GetFileName(sqlPath));
                    streamWriter.Write(File.ReadAllText(sqlPath));
                    streamWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// 讀取Console的輸入字串，超過一定的時間即中斷並回傳結果
        /// </summary>
        /// <param name="millisecondsTimeout">等待輸入的時間，以毫秒計</param>
        /// <returns></returns>
        private static IEnumerable<string> ReadConsoleInput(int millisecondsTimeout)
        {
            while (true)
            {
                var task = Task.Factory.StartNew<string>(Console.ReadLine);
                if (task.Wait(millisecondsTimeout) && !string.IsNullOrWhiteSpace(task.Result))
                    yield return task.Result;
                else
                    break;
            }
        }

        /// <summary>
        /// 壓上版號
        /// </summary>
        private static void StampVersionNumber(IEnumerable<string> assemblyInfoPaths, string versionNumber)
        {
            foreach (string assemblyInfoPath in assemblyInfoPaths)
            {
                // AssemblyInfo.cs 使用 UTF-8-BOM 編碼
                string content = File.ReadAllText(assemblyInfoPath, Encoding.UTF8);
                string pattern = @"(AssemblyFileVersion\("")(\S*)(""\))";
                content = Regex.Replace(content, pattern, string.Format(@"${{1}}{0}${{3}}", versionNumber));
                File.WriteAllText(assemblyInfoPath, content, Encoding.UTF8);
            }
            Console.WriteLine("All modified assemblies have been stamped the new version number: {0}", versionNumber);
        }

        /// <summary>
        /// 從這個執行個體擷取子陣列。 子陣列會在指定的位置開始並繼續到結尾。
        /// </summary>
        private static T[] SubArray<T>(this T[] data, int startIndex)
        {
            return SubArray(data, startIndex, data.Length - startIndex);
        }

        /// <summary>
        /// 從這個執行個體擷取子陣列。 子陣列起始於指定的位置，並且具有指定的長度。
        /// </summary>
        private static T[] SubArray<T>(this T[] data, int startIndex, int length)
        {
            T[] result = new T[length];
            Array.Copy(data, startIndex, result, 0, length);
            return result;
        }
    }
}