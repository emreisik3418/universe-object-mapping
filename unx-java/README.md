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
| `description` | **DescriptionUpdateApp**: bir universe'ün obje açıklamalarını Oracle sorgusundan günceller (aşağıda) |

**Repoda olmayanlar:** `org.tempuri` / `org.datacontract` (WSDL'den üretilmiş Sbox stub'ları) — mevcut projenizdekiler kullanılır.
`SboxCredentialProvider.java` içindeki 4 DVO sınıfının import'u bu yüzden yok: kopyaladıktan sonra Alt+Enter ile ekleyin.

**Classpath (IntelliJ):** `BO_SDK` = sadece `SL SDK\java\sl_sdk.jar` (Compile), `BO_SDK_EDITOR` = SL SDK plugins klasörleri (Provided),
`SOAP_AXIS`, `ORACLE_JDBC`, `JSON`. Projeyi her zaman iç klasörden açın.


---

## Description update (DescriptionUpdateApp)

Bir UNX universe'ün obje açıklamalarını Oracle'daki bir sorgudan günceller ve universe'ü CMS'e geri yayınlar.

**Main class:** `com.akbank.analytics.applications.bo.description.DescriptionUpdateApp`
(VM options ve working directory `UniverseObjectMappingApp` ile aynı)

**Program arguments:**
```
<credentials.json> <universe> <sorgu.sql> [--env HOST:6400] [--apply]
```
- `universe`: CMS yolu (`/Universes/AKBANK/Satis.unx` veya kısaca `AKBANK/Satis`) ya da CUID
- `sorgu.sql`: `OBJECT_PATH` ve `DESCRIPTION` kolonlarını döndüren SELECT (örnek: `description-query.example.sql`)
- `--env`: credentials.json'da birden fazla ortam varsa zorunlu
- `--apply`: **verilmezse DRY-RUN** — universe'e dokunulmaz

**Her çalıştırmada:**
- `reports\description_<universe>_<zaman>.csv`: her obje için DURUM, yol, mevcut ve yeni açıklama (Excel'de açılır)
- `backup\<universe>_<zaman>\`: CMS'teki universe'ün dokunulmamış kopyası (geri dönmek için IDT'den bu `.blx` yayınlanır)
- `logs\description_*.log`

**Durumlar:** `UPDATE` (değişecek), `SAME`, `EMPTY_IN_DB` (DB'de boş → atlanır, mevcut açıklama silinmez),
`NOT_FOUND` (DB'deki yol universe'te yok), `CONFLICT_IN_DB` (aynı yola farklı açıklamalar → atlanır),
`AMBIGUOUS`, `NOT_IN_DB` (universe'te var, sorguda yok → dokunulmaz).

**Eşleştirme:** obje yolu; `/` ve `\` aynı, boşluklar kırpılır; birebir yoksa harf duyarsız (Türkçe I/İ dahil) tekil eşleşme.

**--apply akışı:** retrieve → yedek → sadece `UPDATE` olanları değiştir → save → publish (aynı klasör, üzerine yazar)
→ tekrar indirip doğrula (açıklamalar güncel mi, CUID aynı mı).
