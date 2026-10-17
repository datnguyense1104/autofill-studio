# Triển khai AutoFill Studio trên máy văn phòng

Một container (web + API + worker nền) chạy bằng Docker, ra Internet qua Cloudflare Tunnel. Dữ liệu nằm ở Supabase (Postgres + Storage), máy văn phòng không giữ dữ liệu: hỏng máy chỉ cần cài lại Docker và chép `.env`.

## 1. Chuẩn bị (một lần)

| Việc | Ghi chú |
|---|---|
| Máy | Windows 10/11 hoặc Linux, RAM ≥ 8 GB (LibreOffice + .NET), luôn bật, nên có UPS |
| Docker | Windows: Docker Desktop, bật "Start Docker Desktop when you sign in". Linux: Docker Engine + compose plugin |
| Supabase | Project mới riêng cho AutoFill. Lấy: chuỗi kết nối Session pooler (URI), Project URL, service_role key. Bucket private tự tạo khi app chạy lần đầu |
| AI | `OPENAI_API_KEY` (OpenAI) hoặc key + `OPENAI_BASE_URL` (Azure OpenAI) |
| Tên miền | `tdatnguyen.id.vn` phải do Cloudflare quản lý DNS, **cùng tài khoản** với tunnel (xem mục "Chuyển DNS sang Cloudflare" bên dưới). App chạy ở `https://auto-fill.tdatnguyen.id.vn/`, `BASE_PATH` để trống |
| Cloudflare Tunnel | dash.cloudflare.com → **Networking → Tunnels** → Create tunnel → chạy lệnh cài `cloudflared` cho Windows mà trang đưa ra (dịch vụ Windows, tự chạy khi bật máy). Bấm tên tunnel → tab **Routes** → **Add route** → **Published application**: subdomain `auto-fill`, domain `tdatnguyen.id.vn`, path trống, Service URL `http://localhost:8080` |
| Cách khác cho tunnel | Không cài dịch vụ Windows: chép token vào `CLOUDFLARE_TUNNEL_TOKEN`, Service URL là `http://app:8080`, chạy `docker compose --profile tunnel up -d --build`. Chỉ dùng **một** trong hai cách |

### Chuyển DNS của tdatnguyen.id.vn sang Cloudflare (một lần)

Tên miền đang dùng DNS của PA Việt Nam và đang có site khác, nên phải giữ nguyên các bản ghi cũ:

| Bản ghi hiện có (16/10/2026) | Dùng cho | Trong Cloudflare để |
|---|---|---|
| `A tdatnguyen.id.vn → 216.24.57.1` | site gốc (Render) | DNS only (mây xám) |
| `CNAME www → 7b3d036e19c793fa.vercel-dns-017.com` | site www (Vercel) | DNS only (mây xám) |
| `MX tdatnguyen.id.vn → mail92112.maychuemail.com`, `mx92112.maychuemail.net` | email | (MX không có mây) |
| `CNAME mail → mail92112.maychuemail.com` | webmail | DNS only (mây xám) |

1. dash.cloudflare.com (đúng tài khoản có tunnel) → **Add a domain** → `tdatnguyen.id.vn` → gói **Free**. Cloudflare tự quét bản ghi: đối chiếu với bảng trên, thiếu thì thêm tay, đổi các bản ghi sang **DNS only**. Nếu trang quản lý DNS của PA Việt Nam còn bản ghi TXT (SPF/DKIM cho email) thì chép sang luôn.
2. Cloudflare đưa 2 nameserver (dạng `xxx.ns.cloudflare.com`). Vào trang quản lý tên miền PA Việt Nam → đổi **Nameserver/DNS server** sang 2 địa chỉ đó (không phải sửa bản ghi DNS). Hiệu lực sau vài giờ, tối đa 24–48 giờ; Cloudflare gửi email khi tên miền "Active".
3. Khi Active: thêm route của tunnel như bảng trên. Cloudflare tự tạo bản ghi `auto-fill` trỏ vào tunnel.

## 2. Cài đặt

```bash
git clone <repo> autofill-studio && cd autofill-studio
cp .env.example .env      # điền DATABASE_URL, SUPABASE_*, OPENAI_*, ADMIN_EMAIL; BASE_PATH để trống
docker compose up -d --build
```

Kiểm tra trên máy: `http://localhost:8080/health` trả `{"ok":true}`. Từ ngoài: mở `https://auto-fill.tdatnguyen.id.vn/`.
Đăng ký bằng email trong `ADMIN_EMAIL` → tài khoản quản trị dùng ngay; người khác đăng ký rồi chờ quản trị kích hoạt ở `/admin.html`.

## 3. Cập nhật phiên bản

- Build tại máy: `git pull && docker compose up -d --build`.
- Dùng image GitHub Actions build sẵn (workflow `.github/workflows/docker-image.yml`, chạy khi push lên `main`): đặt `AUTOFILL_IMAGE=ghcr.io/<owner>/<repo>:latest` trong `.env`, sau đó `docker compose pull && docker compose up -d`. Nếu repo private: `docker login ghcr.io` một lần bằng Personal Access Token có quyền `read:packages`.

Database tự migrate khi app khởi động; mọi bảng luôn bật Row Level Security.

## 4. Theo dõi

- Log: `docker compose logs -f app` (mỗi dòng là một JSON).
- Uptime: tạo monitor HTTP (UptimeRobot / Healthchecks.io) gọi `https://auto-fill.tdatnguyen.id.vn/health` mỗi 5 phút. Lỗi 530 từ Cloudflare = máy tắt hoặc tunnel rớt.
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
| `BASE_PATH` | trống | Để trống khi app có tên miền riêng (`auto-fill.tdatnguyen.id.vn`). Chỉ đặt (vd `/auto-fill`) khi app chạy dưới một đường dẫn của site khác; cookie đăng nhập khi đó chỉ gửi trong đường dẫn này |
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
