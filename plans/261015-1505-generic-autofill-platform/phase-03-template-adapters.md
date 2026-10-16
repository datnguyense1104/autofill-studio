# Phase 3 — AI phân tích mẫu + bộ ghi Word/Excel

Status: DONE phần code (2026-10-15) — 51 test .NET + 2 test UI pass. Còn chờ: đo chất lượng AI trên mẫu thật (cần key, xem cuối mục). Khác kế hoạch ban đầu:
- Không có `ITemplateAdapter` chung: Word và Excel mỗi bên 3 file (`*Outline`, `*SlotPlacer`, `*Writer`) + `TemplateFiller` chọn theo loại. `Preview → html` dời sang phase 6 (màn hình duyệt).
- Prompt nằm trong `TemplateAnalyzer.cs` (không tách `AutoFill.Ai/Prompts`), không chứa nghiệp vụ riêng SNV/SFDR; luật riêng đi qua ghi chú của người dùng.
- Slot có **nhiều vị trí** (`Locations`): cụm lặp lại (tên người ở 3 chỗ) cùng một giá trị. Vị trí: `phrase` (đoạn + offset, `WholeParagraph` cho dòng danh sách thêm/bớt được), `section` (vùng thân), `cell`, `rows` (một cột của luật dòng, giá trị theo từng dòng).
- Word: địa chỉ đoạn là số thứ tự phẳng toàn văn bản (thân → header → footer), không dùng `t[k].r[i].c[j]`; ô bảng hiện kèm nhãn "bảng k, hàng i, ô j". AI chỉ chép chữ, code tự tìm offset (bài học SNV). Text box bỏ qua; chữ trong trường Word (DATE...) đọc được nhưng khóa ghi.
- Excel: AI trả ô lẻ + luật dòng; code mở luật thành danh sách dòng có nhãn (B210 ≈ 26 ô + 3 luật thay vì 695 ô). Không bao giờ ghi đè ô có công thức; số/ngày ghi đúng kiểu, mã "0.05" giữ dạng chữ.
- Nhiều bản đã điền: code so khác biệt (LCS theo đoạn, tách dòng sửa vài chữ khỏi khối thân), đưa làm gợi ý; chỗ AI bỏ sót tự thêm với độ tin cậy 0.4.
- Mẫu .doc/.xls được chuyển một lần sang .docx/.xlsx và bản chuyển trở thành file mẫu.
- API: `POST /api/templates {key,name,filledFileIds?,exampleFileIds?,notes?}` → job `template.analyze` (kết quả = analysis); `POST /api/templates/{fileId}/fill {slots,values,highlight}` → file đã điền.
- Đã thử trên mẫu thật (không AI): 8 mẫu SNV đọc đúng; 53 cụm đỏ của 5 mẫu ghi lại đúng 100% từng đoạn; outline B210 ≈ 53K ký tự (ngân sách 60K).
- Hạ tầng test: Postgres nhúng trên Windows treo truy vấn khi một server đã tạo ~16 database → mỗi lớp test DB có server riêng (chạy tuần tự, ~80 giây cả bộ).
- **Chưa làm (cần key AI):** bộ đo 8 mẫu SNV (bỏ chữ đỏ → AI phân tích → so với cụm đỏ, mục tiêu ≥ 90%), SFDR 46 dòng (chưa có repo SFDR trên máy), B210 so với `b210-cell-mapping.csv`.

Mẫu **không cần đánh dấu chữ đỏ**. Người dùng tải file mẫu thật (có thể là một văn bản đã điền sẵn của lần trước) — AI tự xác định đâu là phần cố định, đâu là chỗ thay đổi theo từng lần.

## Mô hình
```
Slot {
  Id, Label, Instructions,
  Location,                          // theo adapter, xem dưới
  ValueType: text | longtext | number | percent | date | boolean | enum,
  Strategy: extract | generate | translate | lookup | constant | formula,
  StrategyConfig (json), Required, Group,
  SampleValue                        // giá trị đang có trong mẫu (nếu mẫu là văn bản đã điền)
}
ITemplateAdapter {
  Detect(file); Outline(file) -> TemplateOutline   // cấu trúc có địa chỉ ổn định
  Write(file, values) -> bytes; Preview(file, values) -> html
}
TemplateAnalyzer: Outline + (tùy chọn) ví dụ nguồn → AI → Slot[] đề xuất + mô tả "dùng khi nào"
```

## Outline có địa chỉ ổn định
- Word: danh sách đoạn `p[i]` (và ô bảng `t[k].r[i].c[j]`) kèm text, kiểu đoạn (văn xuôi/gạch đầu dòng/đánh số), vùng header/footer.
- Excel: mỗi sheet → ô có giá trị/công thức, ô trống cạnh nhãn, cấu trúc cột (header dòng đầu), cột đánh dấu kiểu "x".

## AI phân tích mẫu (TemplateAnalyzer)
Prompt nhận outline (rút gọn ≤ ~15K ký tự, giữ địa chỉ) và trả JSON strict:
- Word: các **cụm thay đổi** `{paragraph, start, end, text}` (tên người, số, ngày, quốc tịch...) và **vùng phần thân** `{fromParagraph, toParagraph}` cần viết lại theo nguồn.
- Excel: ô cần điền theo nhãn, hoặc **luật dòng** (cột điều kiện + cột nhãn + cột đích, cột ngôn ngữ), hoặc bảng ánh xạ.
- Kèm kiểu dữ liệu, strategy gợi ý, hướng dẫn, độ tin cậy.
Nếu người dùng cung cấp 2+ văn bản đã điền cùng mẫu: so sánh khác biệt bằng code (diff đoạn/ô) trước, AI chỉ đặt tên + hướng dẫn → chính xác hơn, rẻ hơn.

## Ghi kết quả
- Word: thay cụm theo offset trong đoạn mà không làm vỡ định dạng (tách run tại biên, giữ rPr của run gốc); phần thân viết lại theo kiểu đoạn mẫu (clone pPr/rPr theo loại đoạn — bài học app SNV). Tùy chọn tô vàng chỗ đã điền.
- Excel: chỉ ghi ô đích, giữ style/công thức; số ghi dạng number.

## Files
`src/AutoFill.Documents/Templates/{Word/*,Excel/*,ITemplateAdapter,Slot,TemplateAnalyzer}.cs`, `src/AutoFill.Ai/Prompts/template-analysis.*`

## Kiểm thử
- 8 mẫu SNV **bản không chữ đỏ** (chuyển chữ đỏ về đen): AI tìm được ≥ 90% cụm từng được đánh dấu đỏ + phần thân đúng vùng (so với đáp án sinh từ bản chữ đỏ).
- SFDR template: luật dòng ra đúng 46 dòng như dự án gốc; ghi cột L/M không đổi ô khác (so sánh toàn workbook).
- B210: so với `b210-cell-mapping.csv`.
- Ghi Word: thay cụm giữa run có định dạng khác nhau vẫn giữ định dạng.

## Rủi ro
- AI bỏ sót/đánh nhầm chỗ cần điền → luôn qua bước người dùng xác nhận (phase 5); hiển thị độ tin cậy thấp nổi bật.
