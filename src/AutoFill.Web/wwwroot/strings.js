// UI text of the recipe builder, the run pane and the review screen, in one place so another
// language can be added later. Vietnamese is the default.
export const T = {
  // Keys are the server's enum names in camelCase.
  valueTypes: { text: 'Chữ ngắn', longText: 'Đoạn dài', number: 'Số', percent: 'Phần trăm', date: 'Ngày', boolean: 'Có/Không', enum: 'Chọn một' },
  strategies: {
    extract: 'Lấy từ nguồn', generate: 'AI viết theo nguồn', translate: 'Dịch',
    lookup: 'Tra bảng tính', constant: 'Giá trị cố định', formula: 'Công thức',
  },
  strategyHelp: {
    extract: 'AI chép đúng thông tin có trong tài liệu nguồn.',
    generate: 'AI viết đoạn văn theo nội dung nguồn và văn phong của mẫu.',
    translate: 'AI dịch nhãn của dòng (luật dòng) hoặc giá trị của một ô khác.',
    lookup: 'Tra trong một bảng tính nguồn (ví dụ EET): tìm khóa ở cột khóa, lấy giá trị ở cột giá trị. Không qua AI.',
    constant: 'Lần nào cũng điền cùng một giá trị. Không qua AI.',
    formula: 'Tính từ ô khác, ví dụ {s1} * 2 hoặc {s1} - {s2}. Không qua AI.',
  },
  ruleTypes: { dateAfter: 'Ngày ở ô này phải sau ngày ở ô', sameSentenceCount: 'Cùng số câu với ô', regex: 'Khớp mẫu (regex)' },
  runStatus: { Queued: 'Đang chờ', Running: 'Đang chạy', NeedsReview: 'Chờ duyệt', Done: 'Đã xuất', failed: 'Lỗi' },
  jobFailed: 'Xử lý không thành công',
  saved: 'Đã lưu',
  confirmLeave: 'Có thay đổi chưa lưu. Rời trang?',
  noSlots: 'Chưa có chỗ cần điền. Bôi chọn chữ trong bản xem trước (Word) hoặc bấm vào ô (Excel) để thêm.',
  lowConfidence: 'AI chưa chắc',
  reviseAsk: 'Cần AI sửa thế nào? (ví dụ: "bổ sung giờ hẹn", "viết ngắn hơn")',
  missingAsk: 'Bổ sung vào ô nào?',
  sourceRead: (pages, ai) => `${pages} trang${ai ? `, AI đọc ${ai} trang scan` : ''}`,
};
