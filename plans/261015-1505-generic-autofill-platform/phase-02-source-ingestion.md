# Phase 2 — Đọc tài liệu nguồn

Status: DONE (2026-10-15) — 38 test .NET + 2 test UI pass. Khác kế hoạch ban đầu:
- Mô hình: `Page.Blocks[]` giữ thứ tự đọc, mỗi block là đoạn văn **hoặc** bảng (không tách `Tables[]` riêng). Ô Excel có địa chỉ (`B7`), sheet là `Label` của trang.
- Scan: chỉ trang thiếu chữ (< 20 ký tự) hoặc font hỏng (ký tự U+FFFD/private-use) mới gửi AI; tách 5 trang/lượt gọi (PdfPig tách trang, đã thử trên PDF scan thật 28 trang). Trang có chữ đọc thẳng.
- Giới hạn 120K ký tự (`MAX_SOURCE_CHARS`) áp dụng khi dựng văn bản cho prompt (`SourceText.Render`), không cắt bản lưu cache; cảnh báo nằm trong `notes`.
- Cache `transcripts` theo (workspace, sha256) + `reader_version`; đổi logic đọc thì tăng `SourceReader.Version`.
- API thử: `POST /api/sources {key,name}` → job `source.read`; `GET /api/sources/{fileId}` → tóm tắt + văn bản + document.
- Word không có trang thật: chỉ tách trang ở ngắt trang tường minh → số trang gần đúng.
- Chưa kiểm được trên máy dev: LibreOffice (.doc/.xls) và Docker build — kiểm ở phase 7; gọi AI thật với file scan chờ anh chạy thử bằng key của anh.
- Chưa nhận ra PDF dùng font TCVN3/VNI (chữ ra sai nhưng vẫn là ký tự hợp lệ) — ghi vào câu hỏi mở.

## Yêu cầu
Mọi nguồn được chuẩn hóa về `SourceDocument`:
```
SourceDocument { FileId, Kind (pdf|docx|xlsx|doc|xls|image), Pages[] }
Page { Number, Blocks[] (paragraph text), Tables[] (rows × cells) }
```
- PDF có lớp chữ: PdfPig, giữ số trang.
- PDF scan / ảnh: gửi AI vision chép lại nguyên văn (bài học từ app SNV: chép trung thực, không dịch, giữ dấu tiếng Việt). Phát hiện scan khi trang có < N ký tự.
- DOCX: đoạn + bảng (OpenXml). XLSX: mỗi sheet là 1 "page", giữ địa chỉ ô (ClosedXML).
- DOC/XLS cũ: chuyển bằng LibreOffice headless (`soffice --convert-to`) trong container, timeout 90s.
- Cache bản chuẩn hóa theo sha256 file (bảng `transcripts`) — không gọi AI 2 lần cho cùng file.
- Văn bản dài: giới hạn cấu hình được (mặc định 120K ký tự), báo cảnh báo khi bị cắt.

## Files
`src/AutoFill.Documents/Ingest/{PdfReader,ScanTranscriber,DocxReader,XlsxReader,LegacyConverter,SourceDocument}.cs`

## Kiểm thử
- Fixtures: PDF scan văn bản đến (SNV), PDF SFDR, Excel EET, file .doc — so sánh số trang, có chứa các chuỗi mốc.
- Cache: chạy 2 lần cùng file chỉ 1 lần gọi AI.

## Rủi ro
- Dữ liệu cá nhân trong file scan được gửi tới AI — ghi rõ trong điều khoản; cho phép cấu hình endpoint Azure (dữ liệu ở region chọn).
