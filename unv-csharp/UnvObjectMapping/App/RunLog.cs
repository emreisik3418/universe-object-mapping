using System;
using System.IO;
using System.Text;

namespace UnvObjectMapping.App
{
    /// <summary>Konsola ve dosyaya aynı anda yazan basit log. Tam stack trace sadece dosyaya yazılır.</summary>
    internal sealed class RunLog : IDisposable
    {
        private readonly object sync = new object();
        private readonly StreamWriter file;

        private RunLog(string path)
        {
            Path = path;
            file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public string Path { get; }

        public static RunLog Open(string directory)
        {
            Directory.CreateDirectory(directory);
            string name = "unv_run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
            return new RunLog(System.IO.Path.Combine(directory, name));
        }

        public void Info(string msg) => Write("INFO ", msg);
        public void Warn(string msg) => Write("WARN ", msg);
        public void Error(string msg) => Write("ERROR", msg);

        public void Error(string msg, Exception ex)
        {
            Write("ERROR", msg + " : " + Describe(ex));
            lock (sync) file.WriteLine(ex);
        }

        public static string Describe(Exception ex)
        {
            Exception root = ex;
            while (root.InnerException != null) root = root.InnerException;
            string s = ex.GetType().Name + ": " + ex.Message;
            return root == ex ? s : s + "  <-  " + root.GetType().Name + ": " + root.Message;
        }

        private void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + msg;
            lock (sync)
            {
                Console.WriteLine(line);
                file.WriteLine(line);
            }
        }

        public void Dispose() => file.Dispose();
    }
}
