using Login_Panel.API;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Totp = Login_Panel.API.Totp;

namespace Login_Panel.Domain.Features.Authentication.Services;

public class AuthenticationService : ControllerBase, IAuthenticationService //TODO: do zmiany ControllerBase
{
    private readonly ILogger<AuthenticationService> _logger;
    private readonly AppDbContext _appDbContext;
    private readonly ILockoutService _lockoutService;
    private readonly IDatabaseService _databaseService;

    public AuthenticationService(ILogger<AuthenticationService> logger, AppDbContext appDbContext,
        ILockoutService lockoutService, IDatabaseService databaseService)
    {
        _logger = logger;
        _appDbContext = appDbContext;
        _lockoutService = lockoutService;
        _databaseService = databaseService;
    }


    public IActionResult LoginHandler(LoginRequest loginRequest)
    {
        var user = _appDbContext.Users
            .Include(user => user.Password)
            .SingleOrDefault(u => u.Login == loginRequest.Login);

        if (_lockoutService.IsUserLockedOut(user, loginRequest.Login)) return NotFound();

        if (user != null && !BCrypt.Net.BCrypt.Verify(loginRequest.Password, user.Password.Secret))
        {
            _databaseService.LogLoginAttempt(user);
            return BadRequest();
        }

        if (user == null)
        {
            _databaseService.LogLoginAttempt(loginRequest.Login);
            return BadRequest();
        }

        var totpToken = _databaseService.CreateTotpToken(user);

        return Ok(new LoginResponse { TotpToken = totpToken });
    }

    public IActionResult TotpHandler(TotpRequest totpRequest)
    {
        var now = DateTime.UtcNow;

        var totpToken = _appDbContext.TotpTokens
            .Include(totpTokens => totpTokens.User)
            .ThenInclude(user => user.Totp)
            .SingleOrDefault(
                t => t.Id == totpRequest.TotpToken
            );

        if (totpToken == null) return BadRequest();

        if (totpToken.Until < now)
        {
            _appDbContext.TotpTokens.Remove(totpToken);
            return BadRequest();
        }

        var totp = new OtpNet.Totp(totpToken.User.Totp.Secret);
        var isValid = totp.VerifyTotp(totpRequest.Secret, out _);

        if (!isValid) return BadRequest();

        var token = _databaseService.GenerateUserToken(totpToken.User);
        _appDbContext.TotpTokens.Remove(totpToken);

        return Ok(new TotpResponse
        {
            Token = token
        });
    }

    public IActionResult LogoutHandler(LogoutRequest logoutRequest)
    {
        if (!Guid.TryParse(logoutRequest.Token, out var token)) return BadRequest();

        var userToken = _appDbContext.UserTokens.SingleOrDefault(t => t.Id == token);

        if (userToken == null) return BadRequest();

        _appDbContext.UserTokens.Remove(userToken);
        _appDbContext.SaveChangesAsync();

        return Ok();
    }

    public IActionResult RegisterHandler(RegisterRequest registerRequest)
    {
        var hashedPassword = BCrypt.Net.BCrypt.HashPassword(registerRequest.Password);

        var password = _appDbContext.Passwords.Add(new Password
        {
            Id = Guid.Empty,
            Secret = hashedPassword
        });

        var totp = _appDbContext.Totps.Add(new Totp
        {
            Id = Guid.Empty,
            Secret = KeyGeneration.GenerateRandomKey(20)
        });

        var user = _appDbContext.Users.Add(new User
        {
            Id = Guid.Empty,
            Email = registerRequest.Email,
            Login = registerRequest.Login,
            Name = registerRequest.Name,
            Surname = registerRequest.Surname,
            Password = password.Entity,
            Totp = totp.Entity
        });

        var totpToken = _databaseService.CreateTotpToken(user.Entity);

        _appDbContext.SaveChanges();

        return Ok(new RegisterResponse
        {
            Secret =
                $"otpauth://totp/Panel:{user.Entity.Login}?secret={Base32Encoding.ToString(totp.Entity.Secret)}&issuer=Panel",
            TotpToken = totpToken
        });
    }
}