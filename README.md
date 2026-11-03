# Lumind API (.NET 10)

Kanser tanısı yeni konmuş yetişkinlere (18+) **Travma Bilgili Bakım** temelli psikososyal destek veren chatbotun .NET 10 Web API'sidir. Bir araştırma projesinin parçasıdır.

Clean Architecture: `Chatbot.Api` (ASP.NET Core, controller tabanlı) · `Chatbot.Application` · `Chatbot.Domain` · `Chatbot.Infrastructure` · `Chatbot.Tests` (xUnit).

> **Durum:** İskelet hazır; şu an yalnızca `GET /api/health` vardır. Veri modeli, kimlik doğrulama, onboarding ve sohbet uç noktaları (SSE ile akan yanıtlar) Faz 2 ile eklenecektir. Yapay zekâ bu API'de **yoktur**; yanıtlar sahte yanıtlayıcıyla üretilecektir.

> **Uyarı:** Bu depo özel (private) bir araştırma projesine aittir. Sistem özel nitelikli kişisel veri (sağlık) işleyecektir. **Railway yalnızca sentetik demo verisi içindir;** gerçek hasta verisi için API ve veritabanının **Türkiye'de barındırılan** bir ortama taşınması gerekir.

## Gereksinimler

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Yerel **PostgreSQL** (elle kurulur; **Docker kullanılmaz**)
- İki veritabanı: `chatbot_dev` ve `chatbot_test`

```sql
CREATE DATABASE chatbot_dev  ENCODING 'UTF8';
CREATE DATABASE chatbot_test ENCODING 'UTF8';
```

## Komutlar

```powershell
dotnet build                                   # uyarılar hata sayılır
dotnet test
dotnet run --project src/Chatbot.Api           # http://localhost:5080 (PORT ortam değişkeni varsa o port)
```

## Gizli değerler (user-secrets)

Bağlantı dizesi ve anahtarlar koda, `appsettings*.json` dosyalarına ya da depoya **girmez**. Geliştirmede `dotnet user-secrets`, yayında ortam değişkenleri kullanılır. Aşağıdaki değerlerdeki `<…>` yer tutucuları kendi değerlerinizle değiştirin:

```powershell
cd api
dotnet user-secrets init --project src/Chatbot.Api
dotnet user-secrets init --project tests/Chatbot.Tests

dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=chatbot_dev;Username=postgres;Password=<PAROLA>" --project src/Chatbot.Api
dotnet user-secrets set "Auth:JwtSigningKey" "<RASTGELE-EN-AZ-32-BAYTLIK-ANAHTAR>" --project src/Chatbot.Api
dotnet user-secrets set "Auth:CodePepper"    "<RASTGELE-GIZLI-DEGER>"               --project src/Chatbot.Api

# Entegrasyon testleri için ayrı bir veritabanı (ortam değişkeni de olabilir):
dotnet user-secrets set "TEST_DB_CONNECTION" "Host=localhost;Port=5432;Database=chatbot_test;Username=postgres;Password=<PAROLA>" --project tests/Chatbot.Tests
```

`appsettings.Development.example.json` dosyasını `appsettings.Development.json` olarak kopyalayabilirsiniz; bu dosya git'e eklenmez.

## Şema yönetimi

Veritabanı şeması **`api/db/` altındaki SQL betikleriyle elle uygulanır** (Faz 2 ile eklenecek). Üretimde **otomatik migration yoktur**; uygulama şemayı kendisi değiştirmez. Betiklerin sırası ve çalıştırma adımları `api/db/README.md` içinde anlatılacaktır.

## Railway notu

Yayın ortamı değişkenleri (`ConnectionStrings__Default`, `Auth__*`, `Cors__AllowedOrigins__0` vb.) Railway panelinden verilir; `PORT` otomatik gelir. Railway ortamında **yalnızca sentetik demo verisi** tutulur.

## İlgili depo

- İstemci: https://github.com/Volkankilic/LumindClient
