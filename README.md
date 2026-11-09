# Lumind API (.NET 10)

Kanser tanısı yeni konmuş yetişkinlere (18+) **Travma Bilgili Bakım** temelli psikososyal destek veren chatbotun .NET 10 Web API'sidir. Bir araştırma projesinin parçasıdır.

Clean Architecture: `Chatbot.Api` (ASP.NET Core, controller tabanlı) · `Chatbot.Application` · `Chatbot.Domain` · `Chatbot.Infrastructure` · `Chatbot.Tests` (xUnit).

> **Durum (Faz 2):** Veri modeli, kod ile kimlik doğrulama, onboarding (aydınlatma, rıza, rumuz, silme) ve SSE ile akan sohbet uç noktaları vardır. Yapay zekâ bu API'de **yoktur**; yanıtları `FakeChatResponder` üretir (istemci mock'undaki şablonların sunucuya taşınmış hâli). Kriz algılama (Faz 5) **kural tabanlıdır** (`RuleCrisisDetector`, Türkçe, dış servis yok; `docs/kriz/kriz-algilama.md`) ve her ortamda kayıtlıdır; Production'da gerçek algılayıcı olmadan API başlamaz. İfade listesi **taslaktır, klinik uzman onayı bekler**; Development'ta eski anahtar kelime yer tutucusu onunla birlikte çalışır.

> **Uyarı:** Bu depo özel (private) bir araştırma projesine aittir. Sistem özel nitelikli kişisel veri (sağlık) işleyecektir. **Railway yalnızca sentetik demo verisi içindir;** gerçek hasta verisi için API ve veritabanının **Türkiye'de barındırılan** bir ortama taşınması gerekir.

## Gereksinimler

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Railway'deki **tek** PostgreSQL veritabanı (yerel kurulum yok, **Docker kullanılmaz**). Yerel geliştirme, testler ve Railway'deki demo aynı veritabanını kullanır; bu yüzden aşağıdaki "Paylaşılan veritabanı kuralları" geçerlidir.

## Komutlar

```powershell
dotnet build                                   # uyarılar hata sayılır
dotnet test                                    # birim + HTTP testleri + gerçek veritabanı testleri (aşağıya bakın)
dotnet run --project src/Chatbot.Api           # http://localhost:5080 (PORT ortam değişkeni varsa o port)
dotnet run --project src/Chatbot.Api -- generate-seed   # api/db/local/seed.sql + 5 YENİ düz kod terminale (yalnızca Development, veritabanına bağlanmaz; şifreleme anahtarı gerekir)
```

### Testler

- `Unit/`, `Http/`: veritabanı gerektirmez (sahte servisler, `WebApplicationFactory`).
- `Integration/`: **gerçek (paylaşılan) Railway veritabanında** çalışır. Bağlantı `ConnectionStrings:Default` (user-secrets ya da ortam değişkeni) ile bulunur; bağlantı yoksa ya da şema sürümü beklenenden düşükse bu testler `[PostgresFact]` ile **atlanır** (atlama nedeni bağlantı bilgisi içermez). Atlanan test sayısına bakın: `Atlanan: 0` olmalı.
  - `AuthIntegrationTests`, `OnboardingAndChatIntegrationTests`, `HealthAndLogIntegrationTests`: kod girişi, refresh rotasyonu, rıza, silme, SSE akışı, idempotency, kriz akışı, günlük sızıntı denetimi.
  - `EncryptionIntegrationTests`, `AuditIntegrationTests`, `RetentionIntegrationTests` (Faz 4): ham SQL'de düz metin olmaması, şifresiz satırın hata olması, denetim satırlarının değiştirilememesi, saklama işinin (kuru koşu, parti, kilit) yalnızca `itest-` kapsamında çalışması.
  - `SchemaDriftTests`: EF modeli ile veritabanını karşılaştırır (tablo, kolon tipi, null'luluk, PK/FK/CHECK adları, ON DELETE, indeks adı/tekilliği/kolonları/kısmi filtresi). Fark varsa neyin eksik olduğunu yazar; çözüm **yeni numaralı bir betik** (`002_*.sql`) ve `verify.sql` güncellemesidir.
- **`REQUIRE_DB_TESTS=1`** (CI, raporlar): veritabanına ulaşılamazsa/şema uygulanmamışsa `Integration/` testleri atlanmaz, **nedenini yazarak başarısız olur**; ortam değişkeni yoksa atlanırlar. Rapor için `$env:REQUIRE_DB_TESTS=1; dotnet test` çalıştırılır ve çıktıda `Atlanan: 0` aranır.
- Testler yalnızca kendi `itest-<guid>` katılımcılarını oluşturur, sonunda FK sırasıyla (rıza, denetim, token, görüşme, katılımcı) siler ve **tohum katılımcılarına dokunmaz**. Test ortamı sahte sırlar kullanır; gerçek pepper'a ihtiyaç yoktur. Başarısız kod denemeleri katılımcıya bağlanamayan **anonim denetim satırları** bırakır (kasıtlı: bir katılımcıya ait olmadığı için güvenle ayırt edilip silinemez).

## Gizli değerler (user-secrets)

Bağlantı dizesi ve anahtarlar koda, `appsettings*.json` dosyalarına ya da depoya **girmez**. `Chatbot.Api` ve `Chatbot.Tests` **aynı `UserSecretsId`'yi** paylaşır, bu yüzden değerleri tek sefer girmek yeter. Aşağıdaki `<…>` yer tutucularını kendi değerlerinizle değiştirin; **parolayı sohbete, loga ya da komut geçmişine yazmayın**:

```powershell
cd api
dotnet user-secrets set "ConnectionStrings:Default" "Host=<proxy-alanadı>;Port=<port>;Database=railway;Username=<kullanıcı>;Password=<PAROLA>;SSL Mode=Require;Trust Server Certificate=true" --project src/Chatbot.Api
dotnet user-secrets set "Auth:JwtSigningKey" "<RASTGELE-EN-AZ-32-BAYT>" --project src/Chatbot.Api
dotnet user-secrets set "Auth:CodePepper"    "<RASTGELE-EN-AZ-32-BAYT>" --project src/Chatbot.Api
dotnet user-secrets set "Privacy:IpHashKey"  "<RASTGELE-EN-AZ-32-BAYT>" --project src/Chatbot.Api
```

**Veri şifreleme anahtarı (Faz 4, zorunlu, Development dahil):** sohbet içeriği ve rumuz veritabanına AES-256-GCM ile şifreli yazılır (ADR 007). Anahtar tam **32 bayt, base64**, `<id>` harf/rakam/`-`/`_`. Üretme komutu çıktıyı **yalnızca panoya** yazar; ekrana ya da sohbete yazdırmayın:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) | Set-Clipboard
cd api
dotnet user-secrets set "Privacy:DataEncryptionKeys:k1" "<PANODAKİ-DEĞER>" --project src/Chatbot.Api
dotnet user-secrets set "Privacy:CurrentDataEncryptionKeyId" "k1" --project src/Chatbot.Api
```

Anahtar yoksa ya da 32 bayt değilse uygulama **başlamaz** (mesaj yalnızca ayar adını söyler, değeri değil). Aynı değer Railway'de `Privacy__DataEncryptionKeys__k1` ve `Privacy__CurrentDataEncryptionKeyId` olarak girilir; **iki yerde birebir aynı olmalıdır**. **Anahtar kaybı veri kaybıdır:** bir şifre yöneticisine de kaydedin. Testler kendi sabit **test anahtarıyla** çalışır (gerçek anahtarı kullanmaz).

**Yönetici imzalama anahtarı (Faz 7, zorunlu, Development dahil):** yönetici paneli belirteçleri **ayrı** bir anahtarla imzalanır (en az 32 bayt, `Auth:JwtSigningKey`'den farklı). Üretme komutu çıktıyı yalnızca panoya yazar:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)) | Set-Clipboard
cd api
dotnet user-secrets set "Admin:JwtSigningKey" "<PANODAKİ-DEĞER>" --project src/Chatbot.Api
```

Railway'de aynı ad `Admin__JwtSigningKey` olur (yerelle aynı olması gerekmez). Anahtar yoksa API **başlamaz**.

**Model ayarları (Faz 6a, isteğe bağlı, VARSAYILAN KAPALI):** `Llm:Provider` verilmezse `Fake` kalır ve hiçbir ayar gerekmez. Gerçek bir model denemek için (yalnızca yerelde, **yalnızca sentetik veriyle**; değerleri kendi sağlayıcınızdan alın, anahtarı sohbete yazmayın):

```powershell
cd api
dotnet user-secrets set "Llm:BaseUrl" "https://<sağlayıcı-alanadı>/v1" --project src/Chatbot.Api
dotnet user-secrets set "Llm:AllowedHosts:0" "<sağlayıcı-alanadı>" --project src/Chatbot.Api
dotnet user-secrets set "Llm:ApiKey" "<ANAHTAR>" --project src/Chatbot.Api
dotnet user-secrets set "Llm:Model" "<model-adı>" --project src/Chatbot.Api
# yalnızca API'nin sohbette modeli kullanması için (değerlendirme komutu buna gerek duymaz):
dotnet user-secrets set "Llm:Provider" "OpenAiCompatible" --project src/Chatbot.Api
```

`Llm:BaseUrl` ana makinesi `Llm:AllowedHosts` listesinde değilse (ya da https değilse) API **başlamaz**; ileti yalnızca ayar adını söyler. İsteğe bağlı ayarlar ve varsayılanları: `Temperature` 0,6, `MaxTokens` 400 (`MaxTokensParameter`: alan adı, varsayılan `max_tokens`), `FirstTokenTimeoutSeconds` 25, `IdleTimeoutSeconds` 20, `TotalTimeoutSeconds` 120, `MaxRetries` 2, `RetryBaseDelayMs` 500, `MaxConcurrentRequests` 8. Railway'de ad biçimi `Llm__ApiKey`, `Llm__AllowedHosts__0` vb. Ayrıntı: [ADR 012](../docs/decisions/012-model-saglayici-baglantisi.md).

### Model değerlendirme komutu (yerel, sentetik veri)

```powershell
cd api
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Chatbot.Api --no-launch-profile -- evaluate-models --scenarios <senaryolar.json> --models "<model-a>,<model-b>"
```

Senaryo dosyasındaki her senaryoyu listedeki her modele, sohbettekiyle aynı parçalardan geçirerek gönderir (kriz algılama → kişisel veri maskeleme → sistem istemi → bağlam kuralları → istemci, aynı zaman aşımı ve yeniden deneme) ve yanıtları ile süreleri bir klasöre yazar. **Veritabanına dokunmaz.** Hedef sunucuyu gösterip `evet` ister. Anahtar yalnızca user-secrets'tan okunur ve hiçbir çıktıya yazılmaz.

- **Gerçek kişisel veri kullanılmaz.** Dosyada `"synthetic": true` bulunmazsa komut reddeder; senaryolar uydurmadır.
- **Kriz algılanan senaryo modele gönderilmez** (`crisis-intercepted` olarak kaydedilir; sohbetteki gibi).
- **Çıktı:** `api/eval-out/<zaman>/results.jsonl` (senaryo, model, ilk parça ms, toplam ms, durum, deneme, yanıt, istem sürümü) ve `summary.md` (model başına medyan/p95). Klasör git'e girmez; `--out` yalnızca `api/eval-out/` altında ya da depo dışında olabilir. İçeriği sohbete, e-postaya ya da repoya koymayın.
- `--prompt <dosya>`: gömülü istem yerine bir istem varyantı dener (sürüm etiketi ve özeti sonuçta kayıtlıdır).

Senaryo dosyası biçimi:

```json
{
  "version": 1,
  "synthetic": true,
  "models": ["isteğe-bağlı-model-adı"],
  "scenarios": [
    { "id": "s01", "title": "kısa başlık",
      "turns": [ { "role": "user", "text": "…" }, { "role": "assistant", "text": "…" }, { "role": "user", "text": "…" } ] }
  ]
}
```

Son tur `user` olmalıdır; `--models` verilirse dosyadaki `models` listesini ezer. Yönetici hesapları yalnızca komut satırından açılır (`create-admin`, `set-admin-password`, `disable-admin`, `enable-admin`; ayrıntı `docs/yonetim-kilavuzu.md`). Komutlar hedef veritabanı sunucu adını gösterip `evet` ister.

`Auth:CodePepper` Railway'deki API ile **aynı** olmalıdır (kod hash'leri pepper'a bağlıdır). Ayrıntılar kök [README.md](../README.md) içindeki "Railway'e yayınlama" bölümündedir. Geliştirme ayarları (`Auth:CookieSecure=false`, `Cors:AllowedOrigins`) için `appsettings.Development.example.json` dosyasını `appsettings.Development.json` olarak kopyalayın; bu dosya git'e eklenmez.

## Uç noktalar

JSON camelCase, enum'lar camelCase string. Hatalar RFC 7807 ProblemDetails + `code` uzantısıdır (`invalid-code`, `unauthorized`, `consent-required`, `not-found`, `conversation-ended`, `stale-document-version`, `too-long`, `empty-message`, `validation-failed`, `rate-limited`, `unavailable`, `unknown`). Ayrıntı ve gerekçeler için bkz. [docs/decisions/004-api-sozlesmesi.md](../docs/decisions/004-api-sozlesmesi.md).

| Uç nokta | Yetki | Açıklama |
|---|---|---|
| `POST /api/auth/code` | açık, IP başına sınırlı | Kod girişi → `{ accessToken, expiresInSeconds, state }` + HttpOnly refresh çerezi |
| `POST /api/auth/refresh` | çerez | Refresh token döndürme (rotation); eski token tekrar gelirse tüm aile iptal |
| `POST /api/auth/logout` | çerez | Oturumu iptal eder, çerezi siler (`204`) |
| `GET /api/me` | `Authenticated` | `ParticipantState` |
| `POST /api/consents` | `Authenticated` | Aydınlatma / rıza / araştırma rızası (1–3 kayıt, atomik) |
| `PUT /api/me/nickname` | `Authenticated` + geçerli rıza | Rumuz |
| `DELETE /api/me` | `Authenticated` | Verileri kalıcı siler, anonim iz kalır |
| `POST /api/conversations` | `OnboardedParticipant` | Yeni görüşme (öncekini sonlandırır) |
| `GET /api/conversations` | `OnboardedParticipant` | Liste (içerik yok) |
| `GET /api/conversations/{id}` | `OnboardedParticipant` | Görüşme + mesajlar |
| `POST /api/conversations/{id}/messages` | `OnboardedParticipant` | Mesaj gönder, yanıt `text/event-stream` |
| `POST /api/conversations/{id}/end` | `OnboardedParticipant` | Görüşmeyi sonlandır (idempotent) |
| `GET /api/health` | açık | `ok` / `not-ready` |

OpenAPI belgesi **yalnızca Development'ta** `GET /openapi/v1.json` adresindedir.

### Yalnızca Development: uçtan uca test katılımcıları

| Uç nokta | Açıklama |
|---|---|
| `POST /api/dev/test-participants` | `{ "state": "new" \| "ready" \| "withdrawn" \| "revoked" \| "outdated" }` → `201 { participantId, code }`. Etiket `itest-e2e-<guid>`; kod rastgele, yalnızca bu yanıtta döner. |
| `DELETE /api/dev/test-participants/{id}` | Katılımcıyı ve bağlı tüm satırlarını (rıza, denetim, token, görüşme, mesaj) siler. `itest-` etiketi olmayanı (tohum dahil) `403 forbidden` ile reddeder, olmayanı `404`. |

`ready`: aydınlatma + rıza + rumuz "Deneme". `withdrawn`: rıza geri alınmış. `revoked`: iptal. `outdated`: eski belge sürümü (`0.0-eski`). Bu uç noktaların servisi Development dışında **DI'a kaydedilmez** ve route'lar yoktur (404); OpenAPI belgesinde görünmezler.

### Hız sınırları (yapılandırılabilir, gizli değil)

| Ayar (ortam değişkeni) | Varsayılan |
|---|---|
| `Auth:CodeAttempts` / `Auth:CodeWindowSeconds` (`Auth__CodeAttempts` …) | 5 / 30 sn, IP başına |
| `Auth:RefreshAttempts` / `Auth:RefreshWindowSeconds` | 30 / 60 sn, IP başına |
| `Chat:MessagesPerMinute` | 20, katılımcı başına |

Uçtan uca testler aynı IP'den çok sayıda giriş yaptığı için API bu sınırlar yükseltilerek başlatılır (örn. `Auth__CodeAttempts=1000`); hız sınırı senaryosu varsayılanlarla ayrı bir koşuda çalışır (bkz. client/README).

## Şema yönetimi

Veritabanı şeması **`api/db/` altındaki SQL betikleriyle elle uygulanır**. Üretimde (ve hiçbir ortamda) **otomatik migration yoktur**; uygulama şemayı kendisi kurmaz ya da değiştirmez (`Migrate()`, `EnsureCreated()` ve EF migration yok). Uygulama başlarken yalnızca `app_schema_version` tablosunu **okur**; sürüm beklenenden düşükse ya da tablo yoksa `GET /api/health` `503 not-ready` döner ve loga "Apply the scripts in api/db/" mesajı yazılır. Betiklerin sırası ve çalıştırma adımları [db/README.md](db/README.md) içindedir. EF modeli ile betikler arasındaki uyumu `Integration/SchemaDriftTests` denetler.

## Paylaşılan veritabanı kuralları

- Kod veritabanında **hiçbir DDL** çalıştırmaz; yalnızca normal uygulama sorguları vardır.
- Testler yalnızca kendi `itest-<guid>` etiketli katılımcılarını oluşturur ve yalnızca onları (çocuk satırlarıyla birlikte, yabancı anahtar sırasına uyarak) siler; tohum katılımcılarına dokunmaz. Çöken testlerin kalıntısı için `db/cleanup-test-data.sql`.
- Veritabanına yalnızca **sentetik/test verisi** girer.

## Railway notu

Üretim (Production) davranışları ve değişken **adları**: [docs/yayin-railway.md](../docs/yayin-railway.md), karar: [ADR 006](../docs/decisions/006-railway-yayini-tek-kok.md). Özet:

- `PORT` varken uygulama `http://[::]:PORT` dinler (IPv4 + IPv6, Railway özel ağı).
- **Production'da `Auth__CookieSecure=false` ise uygulama başlamaz.** Dev uç noktaları, OpenAPI, kriz yer tutucusu ve dev tetikleyicileri Production'da yoktur (testli).
- İstemcinin önündeki Caddy gerçek istemci adresini tek değerli `X-Forwarded-For` olarak yazar; API yönlendirme başlıklarına yalnızca `ForwardedHeaders__KnownNetworks__*` ile verilen **özel ağdan** güvenir (sıçrama sınırı 1). API'nin genel adresi **kapalı** tutulur.

Yayın ortamı değişkenleri kök README'deki "Railway'e yayınlama" bölümündedir. Railway ortamında **yalnızca sentetik demo verisi** tutulur.

## İlgili depo

- İstemci: https://github.com/Volkankilic/LumindClient
