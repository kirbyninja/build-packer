using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BuildPacker
{
    internal static class Program
    {
        public static string VersionNumber { get; private set; }
        public static string VersionName { get; private set; }

        private static void Main(string[] args)
        {
            DateTime versionDate = DateTime.Today;
            int hotfixNumber = 0;

            if (args.Length > 0 && !(Regex.IsMatch(args[0], @"^\d+\.\d+\.\d+\.\d+$") &&
                    DateTime.TryParse(Regex.Match(args[0], @"^\d+\.\d+\.\d+").Value, out versionDate) &&
                    int.TryParse(Regex.Match(args[0], @"\d+$").Value, out hotfixNumber)))
            {
                Console.WriteLine("Please enter the version number in yyyy.MM.dd.# format.");
                return;
            }

            VersionNumber = string.Format("{0:yyyy.M.d}.{1}", versionDate, hotfixNumber);
            VersionName = string.Format("v{0:yyyy.MM.dd}.{1}", versionDate, hotfixNumber);

            string versionDirPath = Path.GetDirectoryName(Directory.GetCurrentDirectory()) + "\\" + VersionName;

            if (Directory.Exists(versionDirPath))
            {
                Console.WriteLine("There already exists a folder with the same name: {0}", VersionName);
                Console.WriteLine("Please try again after deleting/renaming the existed one.");
                return;
            }
            Directory.CreateDirectory(versionDirPath);

            List<string> assemblyInfoPaths = new List<string>();
            List<FileStatus> sqls = new List<FileStatus>();
            string[] diffFiles = Console.In.ReadToEnd().Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

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

            string[] gitDiffs = CheckSqlFormat(sqls);
            string[] assemblies = StampVersionNumber(assemblyInfoPaths, VersionNumber);

            CollectSql(versionDirPath + @"\SQL", sqls);
            MergeSql(versionDirPath + @"\SQL");
            GenerateOutline(versionDirPath, gitDiffs, assemblies);
        }

        /// <summary>
        /// 檢查*.sql檔案的編碼及換行字元，若是Big5或是UTF-8-BOM，則轉碼為UTF-8，若是LF或是CR則轉為CRLF，並回傳git diff字串
        /// </summary>
        private static string[] CheckSqlFormat(List<FileStatus> sqls)
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
                File.WriteAllText(sql.FullPath, Regex.Replace(totalString, @"\r\n?|\n", "\r\n"));
            }
            return sqls.Select(sql => string.Format("{0}   {1}", sql.Status, sql.RelativePath)).ToArray();
        }

        /// <summary>
        /// 將異動到的*.sql檔複製並分類到一資料夾裡的子資料夾
        /// </summary>
        private static void CollectSql(string sqlCollectionPath, List<FileStatus> sqls)
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
        private static void GenerateOutline(string versionDirPath, string[] gitDiffs, string[] assemblies)
        {
            using (StreamWriter sw = new StreamWriter(versionDirPath + @"\ModifiedFiles.txt", false))
            {
                sw.WriteLine("Modified SQL files:");
                sw.WriteLine();
                foreach (string gitDiff in gitDiffs)
                    sw.WriteLine(gitDiff);
                sw.WriteLine();
                sw.WriteLine("Modified assemblies:");
                sw.WriteLine();
                foreach (string assembly in assemblies)
                    sw.WriteLine(assembly);
            }
        }

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
        /// 壓上版號，並回傳dll名稱
        /// </summary>
        private static string[] StampVersionNumber(List<string> assemblyInfoPaths, string versionNumber)
        {
            List<string> modifedAseemblies = new List<string>();
            foreach (string assemblyInfoPath in assemblyInfoPaths)
            {
                // AssemblyInfo.cs 使用 UTF-8-BOM 編碼
                string content = File.ReadAllText(assemblyInfoPath, Encoding.UTF8);
                string pattern = @"AssemblyFileVersion\(""\S*""\)";
                content = Regex.Replace(content, pattern, string.Format(@"AssemblyFileVersion(""{0}"")", versionNumber));
                File.WriteAllText(assemblyInfoPath, content, Encoding.UTF8);

                string[] pathSegments = assemblyInfoPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (pathSegments.Length >= 3)
                    modifedAseemblies.Add(pathSegments[pathSegments.Length - 3]);
            }
            Console.WriteLine("All modified assemblies have been stamped the new version number: {0}", versionNumber);
            return modifedAseemblies.ToArray();
        }

        private static T[] SubArray<T>(this T[] data, int startIndex)
        {
            return SubArray(data, startIndex, data.Length - startIndex);
        }

        private static T[] SubArray<T>(this T[] data, int startIndex, int length)
        {
            T[] result = new T[length];
            Array.Copy(data, startIndex, result, 0, length);
            return result;
        }
    }

    internal class FileStatus
    {
        public FileStatus(string line)
        {
            Status = line.Substring(0, 1);
            RelativePath = line.Substring(2);
            if (RelativePath[0] == '"')
                RelativePath = Regex.Replace(RelativePath.Replace("\"", ""), @"\\\d{1,3}\\\d{1,3}\\\d{1,3}", ConvertOctalEscapeSequenceToUtf8);
        }

        public string FileName { get { return Path.GetFileName(RelativePath); } }

        /// <summary>
        /// With "\" as the directory separator
        /// </summary>
        public string FullPath { get { return Path.GetFullPath(RelativePath); } }

        /// <summary>
        /// With "/" as the directory separator
        /// </summary>
        public string RelativePath { get; private set; }

        /// <summary>
        /// Added (A), Copied (C), Deleted (D), Modified (M), Renamed (R),
        /// have their type (i.e. regular file, symlink, submodule, …​) changed (T),
        /// are Unmerged (U), are Unknown (X), or have had their pairing Broken (B)
        /// </summary>
        public string Status { get; private set; }

        /// <summary>
        /// 將截取到的文字match從Octal Escape Sequence轉換成UTF-8
        /// </summary>
        private static string ConvertOctalEscapeSequenceToUtf8(Match match)
        {
            byte[] b = match.Value.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).
                Select(s => (byte)Convert.ToInt32(s, 8)).ToArray();
            return Encoding.UTF8.GetString(b);
        }
    }
}