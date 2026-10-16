using System;
using CrystalDecisions.Enterprise;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Sdk
{
    /// <summary>
    /// Tek bir CMS oturumu (BO .NET SDK). using bloğu ile kullanılır;
    /// bloktan nasıl çıkılırsa çıkılsın Logoff çalışır.
    /// </summary>
    public sealed class BoSession : IDisposable
    {
        private EnterpriseSession session;

        private BoSession(string cms, EnterpriseSession session, InfoStore infoStore)
        {
            Cms = cms;
            this.session = session;
            InfoStore = infoStore;
        }

        public string Cms { get; }
        public InfoStore InfoStore { get; }

        public static BoSession Logon(string cms, Credential credential, string authType)
        {
            var sessionMgr = new SessionMgr();
            EnterpriseSession es = sessionMgr.Logon(credential.Username, credential.Password, cms, authType);
            try
            {
                EnterpriseService service = es.GetService("InfoStore");
                return new BoSession(cms, es, new InfoStore(service));
            }
            catch
            {
                SafeLogoff(es, cms);   // InfoStore alınamazsa oturumu açık bırakma
                throw;
            }
        }

        public void Dispose()
        {
            if (session == null) return;
            SafeLogoff(session, Cms);
            session = null;
        }

        private static void SafeLogoff(EnterpriseSession es, string cms)
        {
            try { es.Logoff(); }
            catch (Exception ex) { Console.WriteLine("[UYARI] Logoff sırasında hata (" + cms + "): " + ex.Message); }
        }
    }
}
