# Nghiệm thu: 3 công thức thật

Mục tiêu (plan phase 7): tạo 3 công thức **chỉ qua giao diện**, từ mẫu **không đánh dấu**, không sửa code lõi; kết quả ≥ dự án gốc trên cùng bộ file. Cần key AI thật. Nếu một công thức buộc phải sửa code → tổng quát hóa thành tính năng, không làm riêng.

Ghi kết quả từng dòng: ✓ / ✗ + ghi chú. Thời gian và token đọc ở màn hình duyệt (badge token) và bảng `ai_logs`.

## A. SNV — công hàm (thông báo bị hại / trục xuất)

Nguồn: PDF scan văn bản đến. Mẫu: 2–3 file Word đã điền của vụ cũ (bỏ màu đỏ trước khi tải lên). So với: app Soạn văn bản đi.

| # | Bước | Đạt khi |
|---|---|---|
| A1 | Tạo công thức: tải mẫu + văn bản đến của vụ cũ (ô "Tài liệu nguồn của lần đã điền") → Phân tích bằng AI | ≥ 90% cụm từng tô đỏ được đề xuất; phần thân đúng vùng |
| A2 | Soát: thêm/xóa vài chỗ bằng bôi chọn; dòng danh sách người là "Dòng danh sách" | Lưu được, không lỗi |
| A3 | Ghi chú công thức: luật riêng SNV (tên nước tiếng Việt, "Tổng Lãnh sự quán…") | — |
| A4 | Chạy với 1 văn bản đến mới (scan) | Mọi ô có trích dẫn tìm thấy; không còn thông tin vụ cũ trong file xuất |
| A5 | Văn bản đến có 3 người | Danh sách trong file xuất có đúng 3 dòng |
| A6 | Mục "Có thể còn thiếu" | Chỉ nêu thông tin thật sự thiếu; "Bổ sung" ghi được vào phần thân |
| A7 | So với app SNV trên cùng văn bản đến | Không kém hơn |

## B. SFDR NL/FR

Nguồn: PDF quỹ + EET (xlsx). Mẫu: SFDR template xlsx. So với: GTS-GR/automated-translate (cần repo để so 46 dòng).

| # | Bước | Đạt khi |
|---|---|---|
| B1 | Phân tích mẫu | AI đề xuất luật dòng: cột nhãn + cột NL + cột FR |
| B2 | Cột NL/FR: Cách lấy = Dịch, "Dịch sang" = tiếng Hà Lan / tiếng Pháp, Dịch từ = Nhãn của dòng; cùng một Nhóm | Hai cột đi chung một lượt AI |
| B3 | Ô số / Y-N: Cách lấy = Tra bảng tính (File nguồn chứa "EET", cột khóa, cột giá trị) | Không có lượt AI nào cho các ô này |
| B4 | "Kiểm tra thêm sau khi điền": cột FR "Cùng số câu với ô" cột NL | Cảnh báo khi số câu lệch |
| B5 | Số dòng được điền | 46 dòng như dự án gốc |
| B6 | File xuất: chỉ cột L/M thay đổi, ô khác giữ nguyên (so toàn workbook) | ✓ |

## C. B210 Crowe

Nguồn: FS Draft PDF + A210 xlsx. Mẫu: `B210_Crowe Template.xlsx`. So với: `workflow-agent/docs/b210-cell-mapping.csv`.

| # | Bước | Đạt khi |
|---|---|---|
| C1 | Phân tích mẫu | Ô đầu file (B3, B4, K3, L3…) + luật dòng cho G (nhận xét), I (Y/N), L (ảnh hưởng) |
| C2 | Cột I: Kiểu Có/Không, giá trị cho phép Y, N | Giá trị ngoài Y/N bị cảnh báo |
| C3 | So danh sách ô với `b210-cell-mapping.csv` (695 ô) | Đủ ô; ô thừa đánh dấu xóa được |
| C4 | Chạy với FS Draft + A210 | Mọi Y/N có trích dẫn; thời gian < 10 phút |

## D. Chung

| # | Kiểm tra | Đạt khi |
|---|---|---|
| D1 | Workspace A không thấy công thức, lượt chạy, file của workspace B | ✓ (có test tự động) |
| D2 | Tài khoản vai trò "reviewer" không sửa được công thức | ✓ (có test tự động) |
| D3 | PDF scan 40 trang | Chạy nền, giao diện theo dõi tiến độ, không timeout |
| D4 | File trong Supabase Storage chỉ truy cập được qua app (bucket private) | ✓ |
| D5 | Sửa công thức sau khi đã chạy | Lượt chạy cũ vẫn hiện phiên bản cũ |
