# Triển khai AutoFill Studio trên máy văn phòng

Một container (web + API + worker nền) chạy bằng Docker, ra Internet qua Cloudflare Tunnel. Dữ liệu nằm ở Supabase (Postgres + Storage), máy văn phòng không giữ dữ liệu: hỏng máy chỉ cần cài lại Docker và chép `.env`.

## 1. Chuẩn bị (một lần)

| Việc | Ghi chú |
|---|---|
| Máy | Windows 10/11 hoặc Linux, RAM ≥ 8 GB (LibreOffice + .NET), luôn bật, nên có UPS |
| Docker | Windows: Docker Desktop, bật "Start Docker Desktop when you sign in". Linux: Docker Engine + compose plugin |
| Supabase | Project mới riêng cho AutoFill. Lấy: chuỗi kết nối Session pooler (URI), Project URL, service_role key. Bucket private tự tạo khi app chạy lần đầu |
| AI | `OPENAI_API_KEY` (OpenAI) hoặc key + `OPENAI_BASE_URL` (Azure OpenAI) |
| Cloudflare | Zero Trust → Networks → Tunnels (Connectors) → Create tunnel → chạy lệnh cài `cloudflared` cho Windows mà trang đưa ra (cài thành dịch vụ Windows, tự chạy khi bật máy). Sau đó bấm tên tunnel → tab **Published application routes** (tên cũ "Public Hostname") → thêm: subdomain **để trống**, domain `ntdat.qzz.io`, path `auto-fill/*`, service `HTTP` `localhost:8080`. Trong `.env` đặt `BASE_PATH=/auto-fill` |
| Cách khác cho tunnel | Không cài dịch vụ Windows: chép token vào `CLOUDFLARE_TUNNEL_TOKEN`, service route là `app:8080`, chạy `docker compose --profile tunnel up -d --build`. Chỉ dùng **một** trong hai cách |

**Lưu ý khi dùng `ntdat.qzz.io/auto-fill`:** cả tên miền `ntdat.qzz.io` chỉ trỏ được vào **một** tunnel. Nếu trang gốc `ntdat.qzz.io` đang chạy ở chỗ khác (máy khác, Vercel…), phải đưa trang gốc vào cùng tunnel này (thêm một public hostname không có path, đặt sau dòng `auto-fill/*`), hoặc dùng tên miền con riêng (ví dụ `auto-fill.ntdat.qzz.io`, khi đó để `BASE_PATH` trống). Các tên miền con khác như `sfdr-trans.ntdat.qzz.io` không bị ảnh hưởng.

## 2. Cài đặt

```bash
git clone <repo> autofill-studio && cd autofill-studio
cp .env.example .env      # điền DATABASE_URL, SUPABASE_*, OPENAI_*, ADMIN_EMAIL, CLOUDFLARE_TUNNEL_TOKEN
docker compose up -d --build
```

Kiểm tra trên máy: `http://localhost:8080/auto-fill/health` trả `{"ok":true}`. Từ ngoài: mở `https://ntdat.qzz.io/auto-fill/`.
Đăng ký bằng email trong `ADMIN_EMAIL` → tài khoản quản trị dùng ngay; người khác đăng ký rồi chờ quản trị kích hoạt ở `/admin.html`.

## 3. Cập nhật phiên bản

- Build tại máy: `git pull && docker compose up -d --build`.
- Dùng image GitHub Actions build sẵn (workflow `.github/workflows/docker-image.yml`, chạy khi push lên `main`): đặt `AUTOFILL_IMAGE=ghcr.io/<owner>/<repo>:latest` trong `.env`, sau đó `docker compose pull && docker compose up -d`. Nếu repo private: `docker login ghcr.io` một lần bằng Personal Access Token có quyền `read:packages`.

Database tự migrate khi app khởi động; mọi bảng luôn bật Row Level Security.

## 4. Theo dõi

- Log: `docker compose logs -f app` (mỗi dòng là một JSON).
- Uptime: tạo monitor HTTP (UptimeRobot / Healthchecks.io) gọi `https://ntdat.qzz.io/auto-fill/health` mỗi 5 phút. Lỗi 530 từ Cloudflare = máy tắt hoặc tunnel rớt.
- Chi phí AI: bảng `ai_logs` (token theo từng lần gọi, theo job); mỗi lượt chạy hiện tổng token ở màn hình duyệt.

## 5. Biến môi trường

| Biến | Mặc định | Ý nghĩa |
|---|---|---|
| `DATABASE_URL` | — | Chuỗi kết nối Postgres (URI Supabase hoặc dạng Npgsql) |
| `SUPABASE_URL`, `SUPABASE_SERVICE_KEY`, `SUPABASE_BUCKET` | —, —, `autofill` | File lưu ở Storage bucket private |
| `FILE_STORE` | `supabase` khi có `SUPABASE_URL` | `disk` chỉ dùng khi phát triển |
| `MAX_FILE_MB` | 50 | Kích thước file tối đa |
| `OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_BASE_URL` | —, `gpt-6-luna`, trống | AI; đặt `OPENAI_BASE_URL` khi dùng Azure OpenAI |
| `MAX_SOURCE_CHARS` | 120000 | Số ký tự nguồn tối đa đưa vào prompt (dài hơn: cắt và cảnh báo) |
| `BASE_PATH` | trống | Đường dẫn app chạy dưới đó: `/auto-fill` cho `ntdat.qzz.io/auto-fill/`. Cookie đăng nhập chỉ gửi trong đường dẫn này |
| `ADMIN_EMAIL` | — | Tài khoản quản trị |
| `WORKERS` | 2 | Số việc nền chạy song song trong container |
| `SOFFICE_PATH` | `/usr/bin/soffice` trong image | LibreOffice cho .doc/.xls |
| `AUTOFILL_IMAGE`, `CLOUDFLARE_TUNNEL_TOKEN` | | Chỉ cho docker compose |

## 6. Sự cố thường gặp

| Hiện tượng | Xử lý |
|---|---|
| `/health` trả 503 | Không kết nối được Supabase: kiểm tra `DATABASE_URL` (mật khẩu có ký tự đặc biệt phải percent-encode) |
| "Máy chủ chưa được cấu hình AI" | Thiếu `OPENAI_API_KEY` |
| File .doc/.xls báo lỗi LibreOffice | Chỉ xảy ra ngoài Docker; trong image đã có LibreOffice |
| Lượt chạy "Lỗi khi xử lý, hệ thống sẽ thử lại" | Lỗi tạm (mạng, AI quá tải): tự thử lại 3 lần; xem `docker compose logs app` |
