const form = document.getElementById("register-form");
const errorText = document.getElementById("register-error");

// Skickar registreringen till servern
form.addEventListener("submit", async (e) => {
    e.preventDefault();  // stoppar vanlig formulärsändning, så att sidan inte laddas om
    errorText.textContent = "";

    const username = document.getElementById("reg-username").value.trim();
    const password = document.getElementById("reg-password").value;  // trimmas inte: mellanslag kan vara en del av lösenordet
    const confirm = document.getElementById("reg-confirm").value;

    // Snabb kontroll för användaren. Servern gör den riktiga valideringen.
    if (password !== confirm) {
        errorText.textContent = "Lösenorden matchar inte.";
        return;
    }

    const button = form.querySelector("button");
    button.disabled = true; // förhindrar dubbla anrop vid dubbelklick

    try {
        const response = await fetch("/api/auth/register", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
        });

        if (!response.ok) {
            // Serverns felmeddelande visas med textContent, skydd mot XSS (Cross-Site Scripting)
            errorText.textContent = (await response.text()) || "Kontot kunde inte skapas.";
            return;
        }

        // Tillbaka till inloggningen. ?registered=1 gör att chat.js visar "Kontot är skapat".
        window.location.href = "index.html?registered=1";
    } catch (err) {
        console.error(err);
        errorText.textContent = "Kunde inte nå servern.";
    } finally {
        button.disabled = false; // knappen fungerar igen, både vid lyckat och misslyckat anrop
    }
});