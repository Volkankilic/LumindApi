# Veritabanı betikleri (`api/db/`)

Bu klasördeki SQL betikleri şemanın **tek doğruluk kaynağıdır**. Şemayı **yalnızca siz, elle** uygularsınız. API hiçbir ortamda veritabanında DDL çalıştırmaz: `Migrate()`, `EnsureCreated()` ve EF Core migration yoktur. API başlarken yalnızca `app_schema_version` tablosunu **okur**; sürüm beklenenden düşükse ya da tablo yoksa sağlık denetimi "hazır değil" der ve loga "api/db/ betiklerini uygulayın: beklenen sürüm N, bulunan M" yazar.

> **Veri güvenliği:** Railway sunucuları Türkiye'de değildir. Bu veritabanına yalnızca **sentetik/test verisi** girer. Gerçek hasta verisi **asla** girmez (`docs/pilot-oncesi-kontrol-listesi.md` madde 2).

Tek bir Railway PostgreSQL veritabanı vardır (`railway`): yerel geliştirme, otomatik testler ve Railway'deki demo aynı veritabanını kullanır. Testler yalnızca `Label` değeri `itest-` ile başlayan kendi katılımcılarına dokunur; tohum katılımcıları (`seed-1` … `seed-5`) testlerde kullanılmaz.

## Betikler

| Dosya | Ne yapar | Ne zaman |
|---|---|---|
| `001_initial.sql` | Tüm tablolar, kısıtlar, indeksler, sürüm kaydı. Idempotent, tek transaction. | İlk kurulum |
| `verify.sql` | **Salt-okunur** kontrol. Beklenen tablo/kolon/kısıt/indeks ve sürüm kaydı. Tek bir sonuç tablosu döner, son satır `SUMMARY`. | Her betikten sonra |
| `local/seed.sql` | 5 sentetik tohum katılımcı (yalnızca kod **hash**'leri). `generate-seed` ile üretilir, `.gitignore`'dadır. Idempotent. | İsteğe bağlı, `verify.sql` yeşilse |
| `cleanup-test-data.sql` | Çöken entegrasyon testlerinden kalan `itest-` etiketli satırları siler. Yalnızca bu önekle çalışır. | Gerektiğinde |
| `reset-seed-data.sql` | Yalnızca `seed-` etiketli katılımcıları ve bağlı satırlarını siler (veri, DDL değil). Yayın duman testinden (`docs/yayin-duman-testi.md`) sonra, ardından `local/seed.sql` yeniden çalıştırılır. | Elle duman testinden sonra |
| `rollback/001_initial_down.sql` | **YIKICI.** Tüm tabloları ve verilerini siler. | Sıfırdan başlamak istediğinizde |

## Çalıştırma sırası (ilk kurulum)

1. `001_initial.sql`
2. `verify.sql` → son satır `SUMMARY: ALL OK (...)` ve tüm satırlarda `ok = true` olmalı
3. `local/seed.sql` (yalnızca tohum katılımcıları isteniyorsa; önce `dotnet run --project src/Chatbot.Api -- generate-seed` ile üretilir)

## Nasıl çalıştırılır

**A) `psql` ile** (bağlantı dizesini komut geçmişine yazmamak için ortam değişkeni kullanın):

```powershell
psql "$env:RAILWAY_DB_URL" -f api/db/001_initial.sql
psql "$env:RAILWAY_DB_URL" -f api/db/verify.sql
```

**B) GUI araçları (DBeaver, Railway paneli → Data → Query):** dosyanın içeriğini yapıştırıp çalıştırın. DO bloğu kullanılmaz, kısıtlar `CREATE TABLE` içinde (aynı adlarla) tanımlıdır; bu yüzden betik GUI araçlarında da çalışır. DBeaver'da tüm betiği tek seferde çalıştırmak için "Execute script" (Alt+X) kullanın.

Betikler parola, kullanıcı adı, `CREATE DATABASE`, rol, extension ya da düz katılımcı kodu **içermez**.

## Geri alma

`rollback/001_initial_down.sql` her şeyi siler (**yıkıcı**). Sonrasında `001_initial.sql` boş şemayı yeniden kurar.

## Yeni şema değişikliği kuralı

Çalıştırılmış bir betik **asla değiştirilmez**. Her değişiklik yeni numaralı, artımlı bir betiktir:

- `002_<ad>.sql`: koşullu `ALTER TABLE` / `CREATE ... IF NOT EXISTS`, tek transaction, kendi `app_schema_version` kaydı (`INSERT ... ON CONFLICT DO NOTHING`);
- `rollback/002_<ad>_down.sql`;
- `verify.sql` yeni beklentilerle güncellenir;
- uygulamadaki beklenen şema sürümü (`Database:ExpectedSchemaVersion`) ve EF modeli birlikte güncellenir. Drift testi (model ↔ veritabanı) fark varsa kırılır ve hangi betiğin güncellenmesi gerektiğini yazar.

## Sürüm kayıtları

`app_schema_version(version, name, applied_at)`: `001_initial.sql` → `1 / initial`.
