using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Config
{
    /// <summary>
    /// credentials.json okuyucu. Java uygulamasıyla AYNI dosyayı kullanır.
    /// JSON için .NET Framework'ün kendi JavaScriptSerializer'ı kullanılır (ek paket yok).
    /// </summary>
    public sealed class AppConfig
    {
        public string SboxEndpoint { get; private set; }
        public string SboxUsername { get; private set; }
        public string SboxPassword { get; private set; }
        public IList<BoEnvironment> Environments { get; private set; }
        public SboxTarget DbTarget { get; private set; }
        public string DbJdbcUrl { get; private set; }

        public static AppConfig Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Yapılandırma dosyası bulunamadı: " + Path.GetFullPath(path));

            string text = File.ReadAllText(path, Encoding.UTF8);   // BOM varsa otomatik atlanır

            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("credentials.json okunamadı (" + path + "): " + ex.Message, ex);
            }

            var envs = Array(root, "environments")
                .Select(e => new BoEnvironment { Cms = Required(Obj(e, "system"), "cms"), Sbox = Target(e) })
                .ToList();
            if (envs.Count == 0)
                throw new InvalidDataException("credentials.json: 'environments' boş");

            var db = Array(root, "db").FirstOrDefault();
            if (db == null)
                throw new InvalidDataException("credentials.json: 'db' boş");

            return new AppConfig
            {
                SboxEndpoint = Required(root, "endpoint"),
                SboxUsername = Required(root, "username"),
                SboxPassword = Required(root, "password"),
                Environments = envs.AsReadOnly(),
                DbTarget = Target(db),
                DbJdbcUrl = Required(db, "jdbcUrl")
            };
        }

        private static SboxTarget Target(Dictionary<string, object> o) => new SboxTarget
        {
            AppName = Required(o, "appName"),
            SubAppName = Required(o, "subAppName"),
            Ip = Required(o, "ip"),
            SensitiveDataName = Required(o, "sensitiveDataName")
        };

        private static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            object v;
            if (o.TryGetValue(key, out v) && v is Dictionary<string, object>)
                return (Dictionary<string, object>)v;
            throw new InvalidDataException("credentials.json: '" + key + "' nesnesi eksik");
        }

        private static List<Dictionary<string, object>> Array(Dictionary<string, object> o, string key)
        {
            object v;
            if (o.TryGetValue(key, out v) && v is IEnumerable && !(v is string))
                return ((IEnumerable)v).Cast<object>().OfType<Dictionary<string, object>>().ToList();
            throw new InvalidDataException("credentials.json: '" + key + "' dizisi eksik");
        }

        private static string Required(Dictionary<string, object> o, string key)
        {
            object v;
            if (o.TryGetValue(key, out v) && v != null && !string.IsNullOrWhiteSpace(v.ToString()))
                return v.ToString().Trim();
            throw new InvalidDataException("credentials.json: '" + key + "' alanı eksik veya boş");
        }
    }
}
