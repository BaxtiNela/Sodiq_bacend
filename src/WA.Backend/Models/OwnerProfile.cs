namespace WA.Backend.Models;

public class OwnerProfile
{
    public int Id { get; set; }
    /// <summary>Noyob foydalanuvchi identifikatori — lokal saqlanadi, barcha seanslar orqali bir xil</summary>
    public string UserToken { get; set; } = string.Empty;
    public string Name { get; set; } = "Egasi";
    public string Company { get; set; } = string.Empty;
    public string Language { get; set; } = "uz"; // uz, ru, en
    public string Timezone { get; set; } = "Asia/Tashkent";
    public string WorkingHours { get; set; } = "9:00-18:00";

    /// <summary>JSON array: ["dasturlash","musiqa",...]</summary>
    public string InterestsJson { get; set; } = "[]";

    /// <summary>JSON array: tez-tez ochiluvchi dasturlar</summary>
    public string CommonAppsJson { get; set; } = "[]";

    /// <summary>Qo'shimcha ma'lumot: xususiyatlar, uslub, afzalliklar</summary>
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}
