# Phase 7 — Nghiệm thu và deploy

Status: PHẦN CODE XONG (2026-10-16); nghiệm thu 3 recipe thật + deploy lên máy văn phòng chờ anh (cần key AI, Docker, Cloudflare).
- Đã làm: log JSON khi chạy production; `docker-compose.yml` dùng được cả build tại máy lẫn image GHCR (`AUTOFILL_IMAGE`); GitHub Actions `docker-image.yml` (test → build → push GHCR); `docs/deployment.md` (cài, cập nhật, theo dõi, biến môi trường, sự cố); `docs/acceptance-checklist.md` (từng bước nghiệm thu SNV / SFDR / B210 / chung).
- Chưa kiểm được trên máy dev: `docker build` (chưa có Docker), CI trên GitHub (chưa có repo), LibreOffice trong image.
- Font: image có Liberation (tương thích kích thước Times New Roman/Arial) + DejaVu; Times New Roman thật cần giấy phép riêng.

## Nghiệm thu bằng 3 recipe thật (tạo qua UI từ mẫu không đánh dấu, không sửa code lõi)
| Recipe | Mẫu | Nguồn | So với |
|---|---|---|---|
| SNV — thông báo bị hại / trục xuất | 2–3 mẫu Word công hàm | PDF scan văn bản đến | app soan-van-ban-di |
| SFDR NL/FR | SFDR template xlsx | PDF quỹ + EET | GTS-GR/automated-translate |
| B210 Crowe | B210_Crowe Template.xlsx | FS Draft PDF + A210 xlsx | workflow-agent |

Đạt khi: đủ slot như dự án gốc; giá trị đúng ≥ dự án gốc; số/Y-N không qua AI khi có nguồn tra cứu; ghi lại thời gian + chi phí mỗi run.
Nếu phải sửa code lõi cho 1 recipe → tổng quát hóa thành tính năng, không làm riêng.

## Deploy (Docker)
- Dockerfile multi-stage: build `mcr.microsoft.com/dotnet/sdk:10.0` → runtime `aspnet:10.0` + `libreoffice-writer-nogui`, `libreoffice-calc-nogui` (cho .doc/.xls), font Times New Roman/Liberation.
- 1 container (API + UI tĩnh + worker). Supabase Postgres + Storage là dịch vụ ngoài.
- Env: `DATABASE_URL`, `SUPABASE_URL`, `SUPABASE_SERVICE_KEY`, `SUPABASE_BUCKET`, `OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_BASE_URL` (tùy chọn Azure). Không commit bí mật.
- Health check `/health`, log có cấu trúc, `docker-compose.yml` cho dev (kèm Postgres local).

## Máy văn phòng (24/24)
- Cài Docker (Docker Desktop nếu Windows, Docker Engine nếu Linux); container `restart: unless-stopped`; Docker tự chạy khi máy khởi động.
- Ra Internet bằng **Cloudflare Tunnel** (`cloudflared` chạy như container thứ 2 trong compose) → không mở port router, có HTTPS sẵn. Đây cũng là cách backend SFDR đang dùng (`sfdr-trans.ntdat.qzz.io`); lỗi 530 = máy tắt/tunnel rớt → thêm cảnh báo uptime (vd UptimeRobot/Healthchecks ping `/health`).
- Cập nhật: `docker compose pull && docker compose up -d` (image build sẵn đẩy lên GHCR qua GitHub Actions).
- Dữ liệu không nằm trên máy văn phòng (ở Supabase) → máy hỏng chỉ cần cài lại Docker + `.env`.
- Mất điện/mạng = hệ thống ngừng → khuyến nghị UPS.

## Câu hỏi còn mở
1. Máy văn phòng chạy Windows hay Linux? Cấu hình (RAM ≥ 8GB khuyến nghị vì LibreOffice + .NET)? — Địa chỉ chốt 2026-10-17: `https://auto-fill.tdatnguyen.id.vn/` (`ntdat.qzz.io` nằm ở tài khoản Cloudflare khác với tunnel); DNS `tdatnguyen.id.vn` chuyển từ PA Việt Nam sang Cloudflare, giữ bản ghi Render/Vercel/email (bảng trong docs/deployment.md).
2. Nhà cung cấp AI mặc định: OpenAI trực tiếp (gpt-6-luna như SNV) hay Azure OpenAI? Code hỗ trợ cả hai.
3. Tên sản phẩm chính thức (tạm "AutoFill Studio") và repo GitHub (org GTS-GR?).
4. Có tính phí/giới hạn số lượt chạy theo workspace ngay từ đầu không?
5. PDF dùng font cũ TCVN3/VNI (lớp chữ ra "Céng hßa") chưa tự nhận ra để gửi AI đọc lại. Cần thêm nút "Đọc lại bằng AI" hoặc bộ chuyển mã TCVN3/VNI → Unicode? (phát sinh từ phase 2)
