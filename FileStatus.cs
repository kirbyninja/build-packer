using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BuildPacker
{
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