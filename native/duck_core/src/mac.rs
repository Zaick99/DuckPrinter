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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_any_notation() {
        for s in ["00:11:22:AA:BB:CC", "00-11-22-aa-bb-cc", "0011.22AA.BBCC", " 001122aabbcc "] {
            assert_eq!(normalize(s).as_deref(), Some("001122aabbcc"), "{s}");
        }
    }

    #[test]
    fn rejects_garbage() {
        assert_eq!(normalize("001122aabbc"), None);
        assert_eq!(normalize("zz18039debbc"), None);
        assert_eq!(normalize(""), None);
    }

    #[test]
    fn typing_filter() {
        assert_eq!(sanitize_typing("00:11:22:AA:BB:CC:FF"), "001122aabbcc");
    }
}
