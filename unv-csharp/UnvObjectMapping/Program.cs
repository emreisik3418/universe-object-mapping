using System;
using UnvObjectMapping.Config;
using UnvObjectMapping.Credentials;
using UnvObjectMapping.Model;

namespace UnvObjectMapping
{
    internal static class Program
    {
        private const string Usage =
            "Kullanım:\n" +
            "  UnvObjectMapping envcheck\n" +
            "  UnvObjectMapping sbox <credentials.json>";

        private static int Main(string[] args)
        {
            try
            {
                string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                switch (mode)
                {
                    case "envcheck":
                        EnvironmentCheck.Run();
                        return 0;
                    case "sbox":
                        return SboxCheck(Need(args, 1, "credentials.json yolu"));
                    default:
                        Console.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HATA] " + ex);
                return 2;
            }
        }

        private static int SboxCheck(string configPath)
        {
            var config = AppConfig.Load(configPath);
            Console.WriteLine("Ortam sayısı: " + config.Environments.Count);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);

            bool ok = true;
            foreach (BoEnvironment env in config.Environments)
                ok &= Try("BO  " + env.Cms, () => sbox.Get(env.Sbox));
            ok &= Try("DB", () => sbox.Get(config.DbTarget));
            return ok ? 0 : 1;
        }

        private static bool Try(string label, Func<Credential> get)
        {
            try
            {
                Console.WriteLine("[OK]   " + label + " -> " + get());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HATA] " + label + " -> " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("       sebep: " + ex.InnerException.Message);
                return false;
            }
        }

        private static string Need(string[] args, int i, string name)
        {
            if (args.Length <= i) throw new ArgumentException("Eksik parametre: " + name);
            return args[i];
        }
    }
}
