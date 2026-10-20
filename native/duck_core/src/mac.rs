//! Normalizzazione MAC: sempre 12 caratteri esadecimali minuscoli, senza separatori.

/// Restituisce il MAC normalizzato oppure `None` se non sono 12 cifre esadecimali
/// (dopo aver tolto `: - . spazi`).
pub fn normalize(input: &str) -> Option<String> {
    let cleaned: String = input
        .chars()
        .filter(|c| !matches!(c, ':' | '-' | '.') && !c.is_whitespace())
        .map(|c| c.to_ascii_lowercase())
        .collect();
    if cleaned.len() == 12 && cleaned.chars().all(|c| c.is_ascii_hexdigit()) {
        Some(cleaned)
    } else {
        None
    }
}

/// Pulizia "mentre si scrive": toglie tutto cio' che non e' hex, minuscolo, max 12.
pub fn sanitize_typing(input: &str) -> String {
    input
        .chars()
        .filter(|c| c.is_ascii_hexdigit())
        .map(|c| c.to_ascii_lowercase())
        .take(12)
        .collect()
}
