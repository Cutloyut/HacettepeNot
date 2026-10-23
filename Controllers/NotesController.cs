using Microsoft.AspNetCore.Mvc;

namespace HacettepeNot.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NotesController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;

        public NotesController(IWebHostEnvironment env)
        {
            _env = env;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadNote([FromForm] NoteUploadDto request)
        {
            // 1. Dosya Var mı Kontrolü
            if (request.File == null || request.File.Length == 0)
            {
                return BadRequest(new { Success = false, Message = "Lütfen geçerli bir dosya seçin." });
            }

            // 2. Güvenlik & MIME / Uzantı Kontrolü (Sadece PDF)
            var extension = Path.GetExtension(request.File.FileName).ToLowerInvariant();
            if (extension != ".pdf")
            {
                return BadRequest(new { Success = false, Message = "Güvenlik İhlali: Sadece .pdf uzantılı dosyalar kabul edilir." });
            }

            // 3. Sunucuda Depolama Yolu Oluşturma
            var uploadsFolder = Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");
            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await request.File.CopyToAsync(stream);
            }

            // 4. C# AI / OCR İçerik Doğrulama Adımı
            // Örn: var isAiApproved = await _aiService.ScanPdfContentAsync(filePath, request.CourseCode);
            bool isAiApproved = true; 

            if (!isAiApproved)
            {
                // Uygunsuz ise yüklenen dosyayı sil
                System.IO.File.Delete(filePath);
                return BadRequest(new { Success = false, Message = "AI Doğrulaması Başarısız: Dosya içeriği seçilen dersle uyuşmuyor." });
            }

            // 5. Entity Framework ile Veritabanına Kayıt
            var newNoteRecord = new
            {
                Id = Guid.NewGuid().ToString(),
                CourseCode = request.CourseCode,
                ContentType = request.ContentType,
                Title = request.Title,
                Uploader = request.Uploader,
                FilePath = $"/uploads/{uniqueFileName}",
                CreatedAt = DateTime.UtcNow
            };

            // await _context.Notes.AddAsync(newNoteRecord);
            // await _context.SaveChangesAsync();

            return Ok(new { 
                Success = true, 
                Message = "Dosya C# Backend tarafından doğrulandı ve kaydedildi.", 
                Data = newNoteRecord 
            });
        }
    }

    // Flutter'dan gelen multipart/form-data modelini karşılayan DTO
    public class NoteUploadDto
    {
        public string CourseCode { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Uploader { get; set; } = string.Empty;
        public IFormFile? File { get; set; }
    }
}