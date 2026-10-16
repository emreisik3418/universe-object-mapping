# UNX / Java

Kaynaklar: `src/com/akbank/analytics/applications/bo/...` (IntelliJ projenizin `src` klasörüne birebir kopyalanır).

| Paket | İçerik |
|---|---|
| `model` | SboxTarget, BoEnvironment, Credential, UniverseInfo, UniverseObject, UniverseTable, UniverseContent |
| `config` | AppConfig (credentials.json) |
| `credential` | Sbox istemcisi |
| `sdk` | BoSession (garanti logoff), UniverseCatalog (DSL.Universe listesi), UniverseReader (nesne + tablo, tek retrieve) |
| `output` | OracleMetadataWriter (OM tablosu), OracleTableMappingWriter (TM tablosu) — ikisi de sadece `.unx` satırlarına dokunur |
| `app` | UniverseObjectMappingApp (ana), test sınıfları, RunLog, DbMaintenance (rapor) |

**Repoda olmayanlar:** `org.tempuri` / `org.datacontract` (WSDL'den üretilmiş Sbox stub'ları) — mevcut projenizdekiler kullanılır.
`SboxCredentialProvider.java` içindeki 4 DVO sınıfının import'u bu yüzden yok: kopyaladıktan sonra Alt+Enter ile ekleyin.

**Classpath (IntelliJ):** `BO_SDK` = sadece `SL SDK\java\sl_sdk.jar` (Compile), `BO_SDK_EDITOR` = SL SDK plugins klasörleri (Provided),
`SOAP_AXIS`, `ORACLE_JDBC`, `JSON`. Projeyi her zaman iç klasörden açın.
