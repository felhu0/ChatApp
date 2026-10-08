namespace ChatApp.Models;

//Svaret som skickas till webbläsaren när inloggningen lyckas.
public record LoginResponse(string Token, string UserName);
