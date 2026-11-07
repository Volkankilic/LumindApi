# Veritabanı betikleri (`api/db/`)

Bu klasördeki SQL betikleri şemanın **tek doğruluk kaynağıdır**. Şemayı **yalnızca siz, elle** uygularsınız. API hiçbir ortamda veritabanında DDL çalıştırmaz: `Migrate()`, `EnsureCreated()` ve EF Core migration yoktur. API başlarken yalnızca `app_schema_version` tablosunu **okur**; sürüm beklenenden düşükse ya da tablo yoksa sağlık denetimi "hazır değil" der ve loga "api/db/ betiklerini uygulayın: beklenen sürüm N, bulunan M" yazar.

> **Veri güvenliği:** Railway sunucuları Türkiye'de değildir. Bu veritabanına yalnızca **sentetik/test verisi** girer. Gerçek hasta verisi **asla** girmez (`docs/pilot-oncesi-kontrol-listesi.md` madde 2).

Tek bir Railway PostgreSQL veritabanı vardır (`railway`): yerel geliştirme, otomatik testler ve Railway'deki demo aynı veritabanını kullanır. Testler yalnızca `Label` değeri `itest-` ile başlayan kendi katılımcılarına dokunur; tohum katılımcıları (`seed-1` … `seed-5`) testlerde kullanılmaz.

## Betikler

| Dosya | Ne yapar | Ne zaman |
|---|---|---|
| `001_initial.sql` | Tüm tablolar, kısıtlar, indeksler, sürüm kaydı. Idempotent, tek transaction. | İlk kurulum |
| `002_retention_indexes.sql` | **Yalnızca iki indeks** (`audit_logs` zamana göre; saklama işi için) ve sürüm 2 kaydı. Veri ve kısıt değişmez. Idempotent, tek transaction. | `001`/`verify.sql` sonrası (Faz 4) |
| `003_admin.sql` | **Yönetici paneli (Faz 7):** `admin_users`, `admin_refresh_tokens`, `admin_recovery_codes`, `crisis_acknowledgements` tabloları; `participants(label)` benzersiz kısmi indeks, `messages(created_at) WHERE is_crisis` ve `audit_logs(actor_id, action, timestamp)` indeksleri; sürüm 3 kaydı. Idempotent, tek transaction, `DO $` yok. **Çalıştırmadan önce** betiğin başındaki salt-okunur kontrolü yapın (yinelenen etiket var mı). | `002`/`verify.sql` sonrası (Faz 7) |
| `verify.sql` | **Salt-okunur** kontrol. Beklenen tablo/kolon/kısıt/indeks ve sürüm kayıtları (1, 2 ve 3). Tek bir sonuç tablosu döner, son satır `SUMMARY`. | Her betikten sonra |
| `local/seed.sql` | 5 sentetik tohum katılımcı: yalnızca kod **hash**'leri; rumuz ve mesaj metni **şifreli** (Faz 4). `generate-seed` ile üretilir ve her seferinde **yeni rastgele kodlar** seçer; düz kodlar **yalnızca terminale** bir kez yazılır (dosyaya, loga, commit'e girmez). `.gitignore`'dadır. Önce `reset-seed-data.sql`, sonra bu dosya. | İsteğe bağlı, `verify.sql` yeşilse |
| `cleanup-test-data.sql` | Çöken entegrasyon testlerinden kalan `itest-` etiketli satırları siler. Yalnızca bu önekle çalışır. | Gerektiğinde |
| `reset-seed-data.sql` | Yalnızca `seed-` etiketli katılımcıları ve bağlı satırlarını siler (veri, DDL değil). Yayın duman testinden (`docs/yayin-duman-testi.md`) sonra, ardından `local/seed.sql` yeniden çalıştırılır. | Elle duman testinden sonra |
| `rollback/003_admin_down.sql` | **Yönetici tablolarını siler** (hesaplar, oturumlar, kurtarma kodları, ilgilenildi kayıtları) ve eklenen üç indeksi kaldırır; katılımcı, görüşme, mesaj ve denetim satırlarına dokunmaz. | Gerektiğinde |
| `rollback/002_retention_indexes_down.sql` | Yalnızca iki indeksi ve sürüm 2 kaydını siler (veri değişmez). | Gerektiğinde |
| `rollback/001_initial_down.sql` | **YIKICI.** Tüm tabloları ve verilerini siler. | Sıfırdan başlamak istediğinizde |

## Çalıştırma sırası (ilk kurulum)

1. `001_initial.sql`
2. `002_retention_indexes.sql` (Faz 4)
3. `003_admin.sql` (Faz 7)
4. `verify.sql` → son satır `SUMMARY: ALL OK (...)` ve tüm satırlarda `ok = true` olmalı
5. `local/seed.sql` (yalnızca tohum katılımcıları isteniyorsa; önce `dotnet run --project src/Chatbot.Api -- generate-seed` ile üretilir: **şifreleme anahtarı user-secrets'ta tanımlı olmalıdır** ve komut yeni düz kodları terminale yazar)

## Faz 4 geçişi (mevcut veritabanı için, bu sırayla)

Faz 4 ile sohbet içeriği ve rumuz **şifreli** yazılır; şifresiz eski satırlar okunamaz (uygulama onları **hata olarak** görür, sessizce düz metin saymaz). Veritabanında yalnızca sentetik veri olduğu için eski satırlar dönüştürülmez, **sıfırlanır**:

1. **Anahtarı üret ve gir** (`docs/yayin-railway.md` → "Şifreleme anahtarı"): yerelde user-secrets, Railway'de API servisi değişkeni. Aynı değer, aynı `<id>`.
2. **`002_retention_indexes.sql`** çalıştır, ardından `verify.sql` (tümü `ok = true`).
3. **`reset-seed-data.sql`** çalıştır (yalnızca `seed-` katılımcıları ve bağlı satırları silinir). Başka (örn. elle denemelerle oluşmuş) şifresiz içerik varsa o katılımcıların da silinmesi gerekir; kontrol için: `SELECT count(*) FROM public.messages WHERE content NOT LIKE 'enc:v1:%';` ve `SELECT count(*) FROM public.participants WHERE nickname IS NOT NULL AND nickname NOT LIKE 'enc:v1:%';` ikisi de `0` olmalıdır.
4. **`dotnet run --project src/Chatbot.Api -- generate-seed`**: yeni `local/seed.sql` üretilir, **5 yeni düz kod terminale yazılır** (not al). Sonra `local/seed.sql`'i çalıştır, `verify.sql`'i tekrar çalıştır.
5. Ancak bundan sonra yeni API sürümünü **push et / dağıt**.

## Faz 7 geçişi (yönetici paneli, bu sırayla)

API, `ExpectedSchemaVersion = 3` ile gelir: `003_admin.sql` çalışmadan dağıtılırsa sağlık denetimi "hazır değil" der. Ayrıca `Admin__JwtSigningKey` değişkeni olmadan API **başlamaz**.

1. **`Admin__JwtSigningKey`** (yönetici belirteçlerinin ayrı imzalama anahtarı): Railway API servisine **ve** yerelde user-secrets içine, aynı değer (`docs/yayin-railway.md` → "Yönetici imzalama anahtarı").
2. **`003_admin.sql`** (betiğin başındaki yinelenen-etiket kontrolü önce), ardından **`verify.sql`** (tümü `ok = true`).
3. **En az iki yönetici**: `dotnet run --project src/Chatbot.Api --no-launch-profile -- create-admin --username <ad>` (her biri için; ayrıntı `docs/yonetim-kilavuzu.md`).
4. Ancak bundan sonra API'yi (ve istemciyi) **push et / dağıt**.

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

`app_schema_version(version, name, applied_at)`: `001_initial.sql` → `1 / initial`; `002_retention_indexes.sql` → `2 / retention_indexes`; `003_admin.sql` → `3 / admin`.
