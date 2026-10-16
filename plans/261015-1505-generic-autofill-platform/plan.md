# AutoFill Studio — nền tảng điền mẫu tự động bằng AI

Status: CODE XONG cả 7 phase — chờ nghiệm thu với file thật + key AI và deploy máy văn phòng | Created: 2026-10-15 | Updated: 2026-10-16 | Backend: .NET 10 | UI: HTML/CSS/JS thuần (giống app SNV)

## Mục tiêu
Một hệ thống chung cho mọi công việc lặp lại dạng **tài liệu nguồn + file mẫu → AI phân tích, điền → người duyệt → xuất file**.
Khách hàng **tự tạo "công thức" (recipe) qua giao diện**, không cần dev, **không cần đánh dấu chữ đỏ trong mẫu** — AI tự phân tích mẫu để tìm chỗ cần điền.

Kế thừa ý tưởng từ 3 dự án:
- Soạn văn bản đi — Sở Ngoại vụ (Node): viết lại phần thân, đối chiếu thiếu thông tin, chọn mẫu tự động, giao diện.
- automated-translate / SFDR (.NET): Excel theo dòng, dịch NL/FR, tra số từ EET.
- workflow-agent / B210 (.NET): Excel theo bảng ánh xạ ô.

## Quyết định đã chốt (2026-10-15)
| # | Quyết định |
|---|---|
| D1 | Dự án tách riêng hoàn toàn khỏi app SNV |
| D2 | Khách tự tạo recipe qua giao diện |
| D3 | Backend .NET 10 |
| D4 | Mẫu không cần chữ đỏ — AI tự phân tích mẫu, đề xuất chỗ cần điền; người dùng sửa |
| D5 | Giao diện làm giống app SNV: HTML/CSS/JS module thuần, không framework; Node dùng cho tooling/test UI |
| D6 | CSDL PostgreSQL trên Supabase; file lưu Supabase Storage (bucket private) |
| D7 | Deploy bằng Docker |
| D8 | Docker chạy trên máy văn phòng mở 24/24; ra Internet qua Cloudflare Tunnel (không mở port router) |
| D9 | Tạo project Supabase mới riêng cho dự án (không dùng chung app SNV) — anh tự tạo và điền key vào `.env` |

## Khái niệm cốt lõi
| Khái niệm | Ý nghĩa |
|---|---|
| Workspace | Không gian riêng của 1 khách/đơn vị; mọi dữ liệu tách theo workspace |
| Recipe (công thức) | File mẫu + bản phân tích mẫu + danh sách ô + cách lấy giá trị + luật kiểm tra + ví dụ |
| Slot (ô cần điền) | Vị trí trong mẫu (đoạn văn/cụm từ, phần thân, ô Excel, dải dòng) + kiểu + hướng dẫn |
| Strategy | `extract` · `generate` · `translate` · `lookup` · `constant` · `formula` |
| Run (lượt chạy) | Nguồn + recipe → giá trị từng slot kèm bằng chứng → duyệt → xuất |

Nguyên tắc: AI chỉ xử lý chữ; số/mã/Y-N lấy bằng luật hoặc tra bảng. Mọi giá trị có bằng chứng (trang + trích dẫn). Mẫu gốc không bao giờ bị ghi đè.

## Kiến trúc (tóm tắt)
```
[Trình duyệt] UI tĩnh (wwwroot: index.html, *.js module, styles.css)  ← cùng phong cách app SNV
      │  fetch /api/*  (cùng origin, cookie phiên)       upload/download trực tiếp ↔ Supabase Storage (signed URL)
      ▼
AutoFill.Web     ASP.NET Core Minimal API + phục vụ UI tĩnh + SSE tiến độ
AutoFill.Core    domain: Recipe, Slot, Run, strategies, verification
AutoFill.Documents  ingest (PDF/DOCX/XLSX/DOC) + template adapters (Word, Excel) + phân tích mẫu
AutoFill.Ai      IAiClient (OpenAI / Azure OpenAI), prompts, JSON schema strict
AutoFill.Data    Npgsql/EF Core → Supabase Postgres; SupabaseFileStore | DiskFileStore (dev)
AutoFill.Worker  BackgroundService xử lý hàng đợi Run (bảng jobs, FOR UPDATE SKIP LOCKED) — chạy chung process
tests/           xUnit (.NET) + node --test (helper UI thuần)
```
1 Docker image (ASP.NET runtime + LibreOffice headless cho .doc/.xls). Supabase là dịch vụ ngoài.

## Phases
| # | Phase | File | Phụ thuộc |
|---|---|---|---|
| 1 | Nền móng: solution, auth, workspace, Supabase DB + Storage, job queue, AI client, khung UI | [phase-01-foundation.md](phase-01-foundation.md) | — |
| 2 | Đọc nguồn: PDF/scan, DOCX, XLSX, DOC → SourceDocument | [phase-02-source-ingestion.md](phase-02-source-ingestion.md) | 1 |
| 3 | AI phân tích mẫu + bộ ghi Word/Excel, mô hình Slot | [phase-03-template-adapters.md](phase-03-template-adapters.md) | 1, 2 |
| 4 | Engine chạy recipe: strategies, batching, bằng chứng, kiểm tra | [phase-04-run-engine.md](phase-04-run-engine.md) | 2, 3 |
| 5 | UI tạo recipe tự phục vụ | [phase-05-recipe-builder-ui.md](phase-05-recipe-builder-ui.md) | 3, 4 |
| 6 | UI chạy + duyệt + xuất + lịch sử; chọn recipe tự động | [phase-06-run-review-ui.md](phase-06-run-review-ui.md) | 4 |
| 7 | Nghiệm thu bằng 3 recipe thật + deploy Docker | [phase-07-acceptance-deploy.md](phase-07-acceptance-deploy.md) | 5, 6 |

## Tiêu chí nghiệm thu tổng
- Khách tạo được 3 recipe (SNV công hàm, SFDR NL/FR, B210) **hoàn toàn qua UI**, từ file mẫu **không đánh dấu**, không sửa code lõi.
- Kết quả mỗi recipe ≥ chất lượng dự án gốc trên cùng bộ file mẫu.
- Mỗi giá trị hiển thị bằng chứng; số/Y-N không qua AI khi có nguồn tra cứu.
- Dữ liệu tách theo workspace; file trong bucket private, chỉ truy cập qua signed URL ngắn hạn.
- Lượt chạy dài (PDF 40 trang) chạy nền, UI theo dõi tiến độ, không timeout HTTP.

## Câu hỏi còn mở
Xem cuối [phase-07-acceptance-deploy.md](phase-07-acceptance-deploy.md#câu-hỏi-còn-mở).
