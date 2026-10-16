# Phase 1 — Nền móng

Status: DONE (2026-10-15) — 21 test .NET + 2 test UI pass. Khác kế hoạch ban đầu:
- Đăng nhập tự viết (PasswordHasher + cookie) thay vì ASP.NET Identity đầy đủ — gọn hơn, đủ nhu cầu; tài khoản mới chờ quản trị kích hoạt, email trong `ADMIN_EMAIL` là quản trị.
- Dev chạy thẳng với Supabase (`.env`); Postgres nhúng chỉ dùng cho test.
- RLS bật tự động cho mọi bảng sau mỗi lần migrate (bảng mới ở phase sau không bị quên).
- LibreOffice chưa có trong Docker image — thêm ở phase 2 khi cần đọc .doc/.xls.
- Gói test `MysticMind.PostgresEmbed` cần SharpCompress 0.34 (cảnh báo GHSA-6c8g-7p36-r338 được bỏ qua chỉ trong project test, lý do ghi trong csproj).

## Yêu cầu
- Solution .NET 10, các project như trong plan.md; `Directory.Build.props` bật nullable, warnings-as-errors.
- Đăng nhập: ASP.NET Core Identity (email + mật khẩu) lưu trong Supabase Postgres, cookie HttpOnly/Secure/SameSite=Lax; khóa tạm khi sai nhiều lần.
- Workspace: user thuộc 1..n workspace, vai trò `owner | editor | reviewer`. Mọi truy vấn lọc theo workspace (EF global query filter). Bật RLS trên mọi bảng (chặn truy cập qua API công khai của Supabase).
- CSDL: Supabase Postgres qua Npgsql (pooler, TLS verify-full với CA Supabase — như app SNV). EF Core migrations. Dev/test local: Postgres nhúng (gói `MysticMind.PostgresEmbedded`, tự tải binary) — máy dev chưa có Docker; hoặc trỏ `DATABASE_URL` vào Supabase.
- File store: `IFileStore` — `SupabaseFileStore` (bucket private, REST bằng service key; signed upload/download URL để trình duyệt gửi file thẳng) và `DiskFileStore` (dev/test). Key `ws/{workspaceId}/{sha256}.{ext}`, `uploads/{workspaceId}/{uuid}.{ext}`.
- Hàng đợi: bảng `jobs` (status, payload, attempts, locked_until); `BackgroundService` lấy việc bằng `FOR UPDATE SKIP LOCKED`. Tiến độ đẩy về UI qua Server-Sent Events.
- AI client: `IAiClient.GenerateJson<T>(prompt, schema, files?)` trên gói `OpenAI` chính thức; endpoint/model qua env (OpenAI hoặc Azure OpenAI). Log token + chi phí vào `ai_logs`.
- Khung UI trong `src/AutoFill.Web/wwwroot`: header, menu người dùng, đăng nhập/đăng ký, bố cục 2 cột — tái dùng phong cách `styles.css`/`ui.js` của app SNV (viết lại, không link chéo repo).
- Bí mật chỉ qua env / user-secrets; `.env*` trong `.gitignore`.

## Bảng dữ liệu chính
`users (Identity), workspaces, memberships, recipes, recipe_versions (template_file, analysis json, slots json), recipe_examples, files, transcripts, runs, run_values, jobs, ai_logs, audit_log`

## Files tạo mới
`AutoFill.sln`, `src/AutoFill.{Web,Core,Documents,Ai,Data}/`, `src/AutoFill.Web/wwwroot/{index.html,styles.css,ui.js,app.js}`, `tests/AutoFill.Tests/`, `tests/ui/*.test.js`, `docker-compose.yml`, `Dockerfile`, `.gitignore`, `.env.example`, `README.md`.

## Kiểm thử
- Tích hợp: user A không đọc được recipe/file của workspace B.
- Queue: 2 worker không lấy trùng 1 job; job lỗi retry tối đa 3 lần.
- `dotnet build` + `dotnet test` + `node --test tests/ui` sạch.

## Rủi ro
- Pooler Supabase (transaction mode) không hỗ trợ prepared statements lâu dài → cấu hình Npgsql `No Reset On Close`/`Max Auto Prepare=0` khi dùng cổng 6543.
