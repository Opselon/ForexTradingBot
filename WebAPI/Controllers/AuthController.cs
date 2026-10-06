using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public AuthController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public class LoginModel
        {
            [Required]
            public string? Username { get; set; }

            [Required]
            public string? Password { get; set; }
        }

        [HttpPost("login")]
        [AllowAnonymous] // Allow access to login even if not authenticated
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Login([FromBody] LoginModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            string? adminUsername = _configuration["Admin:Username"];
            string? adminPassword = _configuration["Admin:Password"];

            if (string.IsNullOrEmpty(adminUsername) || string.IsNullOrEmpty(adminPassword))
            {
                // This indicates a server configuration issue
                // Log this error appropriately in a real application
                return StatusCode(StatusCodes.Status500InternalServerError, "Admin credentials not configured on the server.");
            }

            if (model.Username == adminUsername && model.Password == adminPassword)
            {
                List<Claim> claims =
                [
                    new(ClaimTypes.Name, model.Username!),
                    new(ClaimTypes.Role, "Admin"),
                    // Server-side stamp: lets a logout invalidate cookies issued
                    // before it (see AuthSecurityStamp).
                    new(WebAPI.Security.AuthSecurityStamp.Claim, WebAPI.Security.AuthSecurityStamp.Current()),
                    // Add other claims as needed
                ];

                ClaimsIdentity claimsIdentity = new(
                    claims, CookieAuthenticationDefaults.AuthenticationScheme);

                AuthenticationProperties authProperties = new()
                {
                    //AllowRefresh = <bool>,
                    // Refreshing the authentication session should be allowed.

                    //ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(60),
                    // The time at which the authentication ticket expires. A
                    // value set here overrides the ExpireTimeSpan option of
                    // CookieAuthenticationOptions set with AddCookie.

                    IsPersistent = true,
                    // Whether the authentication session is persisted across
                    // multiple requests. Required when dealing with cookies.

                    //IssuedUtc = <DateTimeOffset>,
                    // The time at which the authentication ticket was issued.

                    //RedirectUri = <string>
                    // The full path or absolute URI to be used as an http
                    // redirect response value.
                };

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    authProperties);

                return Ok(new { Message = "Login successful" });
            }

            return Unauthorized(new { Message = "Invalid username or password" });
        }

        [HttpPost("logout")]
        [Authorize] // Only authenticated users can logout
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // Rotate the server-side stamp so cookies issued before this logout stop
            // authenticating, not just the one this caller presented.
            WebAPI.Security.AuthSecurityStamp.Rotate();
            return Ok(new { Message = "Logout successful" });
        }
    }
}
