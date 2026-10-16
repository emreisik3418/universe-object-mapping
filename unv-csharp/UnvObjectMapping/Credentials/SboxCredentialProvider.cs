using System;
using System.ServiceModel;
using UnvObjectMapping.Model;
using UnvObjectMapping.Sbox;

namespace UnvObjectMapping.Credentials
{
    public sealed class CredentialException : Exception
    {
        public CredentialException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Kimlik bilgisini Sbox (HsmService, WCF) servisinden alır.
    /// UnvObjectMapping.Sbox namespace'indeki proxy sınıfları tools\generate-sbox-proxy.bat ile üretilir.
    /// </summary>
    public sealed class SboxCredentialProvider
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly string endpoint;
        private readonly string sboxUser;
        private readonly string sboxPassword;

        public SboxCredentialProvider(string endpoint, string sboxUser, string sboxPassword)
        {
            this.endpoint = endpoint;
            this.sboxUser = sboxUser;
            this.sboxPassword = sboxPassword;
        }

        public Credential Get(SboxTarget t)
        {
            var binding = new BasicHttpBinding(endpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                ? BasicHttpSecurityMode.Transport
                : BasicHttpSecurityMode.None)
            {
                OpenTimeout = Timeout,
                SendTimeout = Timeout,
                ReceiveTimeout = Timeout,
                CloseTimeout = Timeout,
                MaxReceivedMessageSize = 10 * 1024 * 1024
            };
            var client = new HsmServiceClient(binding, new EndpointAddress(endpoint));

            OpSysSensDataListDVO response;
            try
            {
                var request = new OpSysSensDataNameRequest
                {
                    AppName = t.AppName,
                    SubAppName = t.SubAppName,
                    SensitiveDataName = t.SensitiveDataName,
                    OperationRequest = new OpRequestDVO
                    {
                        ClientIP = t.Ip,
                        ServerIP = t.Ip,
                        KullName = sboxUser,
                        KullPass = sboxPassword
                    }
                };
                response = client.GetParametersWithName(request);
                client.Close();
            }
            catch (Exception ex)
            {
                client.Abort();   // hatalı WCF kanalı Close ile değil Abort ile kapatılır
                throw new CredentialException("Sbox çağrısı başarısız: " + t, ex);
            }

            if (response == null)
                throw new CredentialException("Sbox boş yanıt döndü: " + t);
            if (response.OperationResult != null && !string.IsNullOrEmpty(response.OperationResult.UserMsg))
                throw new CredentialException("Sbox hata döndü (" + t + "): " + response.OperationResult.UserMsg);

            if (response.SensDataList != null)
            {
                foreach (var sd in response.SensDataList)
                {
                    if (sd != null && t.SensitiveDataName == sd.MBL_SENSDATA_NAME)
                        return Parse(sd.MBL_SENSDATA_ENCRYPTED_VALUE, t);
                }
            }
            throw new CredentialException("Sbox'ta kayıt bulunamadı: " + t);
        }

        /// <summary>Beklenen format "kullanici;sifre". Şifrede ';' varsa korunur.</summary>
        private static Credential Parse(string value, SboxTarget t)
        {
            int idx = value == null ? -1 : value.IndexOf(';');
            if (idx <= 0 || idx == value.Length - 1)
                throw new CredentialException("Sbox değeri 'kullanici;sifre' formatında değil: " + t);
            return new Credential(value.Substring(0, idx), value.Substring(idx + 1));
        }
    }
}
