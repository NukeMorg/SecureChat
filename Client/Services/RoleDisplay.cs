namespace SecureChat.Client.Services;

/// <summary>
/// Отображаемое имя роли: внутренний код протокола (Admin/HR/Employee)
/// показывается пользователю как «Admin / Менеджер / Employee».
/// </summary>
public static class RoleDisplay
{
    public static string Display(string role) => role switch
    {
        "HR" => "Менеджер",
        _ => role
    };
}
