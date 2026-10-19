# Universe Object Mapping

SAP BusinessObjects 4.3 universe'lerindeki nesnelerin (dimension, measure, detail…) SELECT/WHERE
tanımlarını okuyup Oracle'daki `POPR.SDK_METADATA_UNIVERSEOM_JOB` tablosuna yazar.

| Klasör | Ne | Teknoloji |
|---|---|---|
| `unv-csharp/` | UNV (Universe Design Tool) universe'leri | C#, .NET Framework 4.8, x86 |
| `unx-java/` | UNX (Information Design Tool) universe'leri — object + table mapping | Java 8 (SAP JVM), SL SDK |

Her iki uygulama da aynı `credentials.json` dosyasını kullanır (örnek: `credentials.example.json`).
Gerçek `credentials.json` **asla** repoya eklenmez.

---

## UNV / C# — kurulum

### Ön koşullar
- Visual Studio 2019/2022 (.NET Framework 4.8 targeting pack)
- BO 4.3 Client Tools (Universe Design Tool dahil) — sunucuyla aynı patch seviyesi

### 1. Sbox proxy'sini üretin (makine başına bir kez)
**Developer Command Prompt for VS** açın:
```
cd <repo>\unv-csharp\tools
generate-sbox-proxy.bat "<credentials.json'daki endpoint>?wsdl"
```
`UnvObjectMapping\Sbox\HsmServiceProxy.cs` oluşur. Bu dosya olmadan proje derlenmez.
WSDL adresine komut satırından erişilemiyorsa tarayıcıda açıp `.wsdl` olarak kaydedin ve dosya yolunu verin.

### 2. Projeyi açın
`unv-csharp\UnvObjectMapping.sln` → platform **x86** seçili olmalı → **Build → Rebuild Solution**.

### 3. Test modları
Project → Properties → Debug → *Command line arguments*:

| Argüman | Ne yapar |
|---|---|
| `envcheck` | Süreç 32-bit mi, BO .NET SDK GAC_32'den mi yükleniyor, Designer açılıp kapanıyor mu |
| `sbox C:\...\credentials.json` | Her ortam + DB için Sbox'tan kimlik bilgisi alınabiliyor mu |
| `logon C:\...\credentials.json` | Her ortamda CMS logon + UNV listesi |
| `read C:\...\credentials.json [ad]` | Tek UNV'yi Designer ile okur |
| `dbtest C:\...\credentials.json [ad]` | Tek UNV'yi `DBTEST_UNV` olarak yazar, sayar, siler (gerçek veriye dokunmaz) |
| `dbreport C:\...\credentials.json` | OM ve TM tablolarının SYSTEM / UNX-UNV / zaman özeti (salt okuma) |
| **`run C:\...\credentials.json`** | **Ana çalıştırma:** tüm ortamlar, tüm UNV'ler → Oracle. Log: `logs\unv_run_*.log`. Çıkış kodu 0/1/2 |
| `retry C:\...\credentials.json [log]` | Sadece verilen log'da hatalı görünen UNV'leri (`HATA` / `ATLANDI` / `Tablolar okunamadı`) tekrar okur ve DB'de **sadece onların** satırlarını yeniler. Log'da ortam düzeyinde hata varsa (ör. "Designer açılamadı, kalan N UNV atlandı") eksikler DB'den bulunur: log başlangıcından beri yazılmamış her UNV. Log verilmezse `logs\` altındaki en yeni `unv_*.log`. Kendi log'u `logs\unv_retry_*.log` |

### Neden böyle? (bulunan tuzaklar)
- **x86 zorunlu:** `CrystalDecisions.Enterprise.*` sadece `GAC_32`'de.
- **Designer `dynamic` ile kullanılır, Interop.Designer YOK:** Designer 64-bit ayrı süreç
  (`win64_x64\designer.exe /Automation`). Tip kütüphanesi sadece 64-bit kayıtlı olduğu için
  x86'dan tipli arayüz çağrısı `E_NOINTERFACE` verir; `IDispatch` (geç bağlama) sorunsuz çalışır.
- **CrystalDecisions referansları:** `Embed Interop Types = False` (aksi halde CS1752),
  `Copy Local = False` (her zaman GAC sürümü).
- **Universe universe yazılır:** her universe okununca kendi satırları tek transaction'da yenilenir.
  Bütün ortamı bellekte toplamak 32-bit süreçte `OutOfMemoryException` veriyordu (GMP03 ~750 bin nesne).
  CMS'ten silinmiş universe'lerin satırları run sonunda temizlenir.
- **Designer garanti kapatılır:** `Quit()` + `FinalReleaseComObject`, kapanmazsa sadece
  bizim başlattığımız PID sonlandırılır → arka planda `designer.exe` birikmez.

---

## Tablo kuralı (iki uygulama birlikte)
- Java sadece `UNIVERSE_NAME LIKE '%.unx'` satırlarını, C# sadece diğerlerini siler/yeniler.
- `SYSTEM` kolonu = `credentials.json`'daki `cms` değeri (`SUNUCU:6400`).
