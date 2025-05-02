namespace Login_Panel.Domain.Features.Authentication.DTOs;

public class RegisterResponse
{
    public required string Secret { get; init; }
    public required Guid TotpToken { get; set; }
}