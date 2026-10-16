# AutoFill Studio

Điền mẫu Word/Excel tự động bằng AI từ tài liệu nguồn (PDF, scan, Word, Excel), người dùng duyệt lại rồi xuất file.
Khách tự tạo "công thức" (recipe = file mẫu + chỗ cần điền + cách lấy giá trị) qua giao diện.

Kế hoạch: [plans/261015-1505-generic-autofill-platform/plan.md](plans/261015-1505-generic-autofill-platform/plan.md)

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `src/AutoFill.Core` | Thực thể, interface (file store, AI, job handler) |
| `src/AutoFill.Data` | EF Core + PostgreSQL (Supabase), migrations, file store (Supabase Storage / ổ đĩa), hàng đợi job |
| `src/AutoFill.Documents` | `Ingest/`: đọc tài liệu nguồn (PDF có chữ, scan/ảnh qua AI, Word, Excel, .doc/.xls qua LibreOffice) → `SourceDocument`. `Templates/`: AI phân tích mẫu chưa đánh dấu → slot; ghi giá trị vào Word/Excel giữ định dạng. `Runs/`: engine chạy recipe (luật, AI theo lượt, kiểm tra bằng chứng) |
| `src/AutoFill.Ai` | Gọi OpenAI / Azure OpenAI với JSON schema strict, ghi log chi phí |
| `src/AutoFill.Web` | ASP.NET Core: API, đăng nhập, worker nền, giao diện tĩnh trong `wwwroot/` (HTML/CSS/JS thuần) |
| `tests/AutoFill.Tests` | xUnit, chạy trên PostgreSQL nhúng (tự tải lần đầu) |
| `tests/ui` | Test helper giao diện bằng `node --test` |

## Chạy trên máy dev

Cần .NET 10 SDK và Node 24 (chỉ để chạy test UI).

```bash
cp .env.example .env              # điền DATABASE_URL, SUPABASE_*, OPENAI_API_KEY, ADMIN_EMAIL
dotnet run --project src/AutoFill.Web
```

Mở trang (khi `.env` có `BASE_PATH=/auto-fill` thì địa chỉ là `http://localhost:<cổng>/auto-fill/`), đăng ký bằng email trong `ADMIN_EMAIL` → tài khoản quản trị dùng được ngay; tài khoản khác chờ quản trị kích hoạt ở `/admin.html`.
Cơ sở dữ liệu tự migrate khi khởi động; mọi bảng được bật Row Level Security (chặn Data API công khai của Supabase).

File .doc/.xls cần LibreOffice (đã có sẵn trong Docker image). Trên máy dev không cài LibreOffice thì chỉ hai loại này báo lỗi; test chuyển đổi tự bỏ qua.

## Test

```bash
dotnet test        # .NET (PostgreSQL nhúng, không cần Docker)
npm test           # helper giao diện
```

## Thêm migration

```bash
dotnet tool restore
dotnet ef migrations add <Ten> --project src/AutoFill.Data --startup-project src/AutoFill.Web --output-dir Migrations
```

## Các trang

| Trang | Dùng để |
|---|---|
| `/` | Danh sách công thức; thả tài liệu nguồn, chọn/gợi ý công thức, điền mẫu; lịch sử |
| `/recipe.html` | Tạo/sửa công thức: tải mẫu → AI phân tích → soát chỗ cần điền trên bản xem trước → lưu (mỗi lần lưu là một phiên bản) |
| `/run.html?id=…` | Duyệt kết quả: giá trị + trích dẫn nguồn + cảnh báo, sửa, AI viết lại, xuất file |
| `/admin.html` | Quản trị kích hoạt/khóa tài khoản |

## Deploy và nghiệm thu

- Triển khai trên máy văn phòng (Docker + Cloudflare Tunnel): [docs/deployment.md](docs/deployment.md)
- Nghiệm thu bằng 3 công thức thật: [docs/acceptance-checklist.md](docs/acceptance-checklist.md)
