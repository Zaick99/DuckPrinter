//! Parsing dell'elenco host incollato: separatori liberi, validazione, deduplica case-insensitive.

use serde::Serialize;
use std::collections::HashSet;
use std::net::{Ipv4Addr, Ipv6Addr};

#[derive(Debug, Default, Serialize, PartialEq)]
pub struct ParsedHosts {
    pub valid: Vec<String>,
    pub invalid: Vec<String>,
    pub duplicates: usize,
}

fn is_separator(c: char) -> bool {
    c.is_whitespace() || matches!(c, ',' | ';' | '|')
}

pub fn is_valid_host(s: &str) -> bool {
    if s.is_empty() || s.len() > 253 {
        return false;
    }
    if s.parse::<Ipv4Addr>().is_ok() || s.parse::<Ipv6Addr>().is_ok() {
        return true;
    }
    // "1.2.3" / "1.2.3.999": sembra un IP ma non lo e'
    if s.chars().all(|c| c.is_ascii_digit() || c == '.') {
        return false;
    }
    s.split('.').all(|label| {
        !label.is_empty()
            && label.len() <= 63
            && !label.starts_with('-')
            && !label.ends_with('-')
            && label.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
    })
}

pub fn parse(text: &str) -> ParsedHosts {
    let mut out = ParsedHosts::default();
    let mut seen: HashSet<String> = HashSet::new();
    for raw in text.split(is_separator) {
        let token = raw
            .trim_matches(|c: char| matches!(c, '"' | '\'' | '\u{feff}'))
            .trim_start_matches('\\')
            .trim_end_matches('.');
        if token.is_empty() {
            continue;
        }
        if !is_valid_host(token) {
            out.invalid.push(token.to_string());
            continue;
        }
        if seen.insert(token.to_ascii_lowercase()) {
            out.valid.push(token.to_string());
        } else {
            out.duplicates += 1;
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_dedups_and_validates() {
        let p = parse("PC001\r\npc001, pc002.example.local; 10.1.1.5 \\\\SRV01 bad host! 999.1.1.1 -x");
        assert_eq!(p.valid, vec!["PC001", "pc002.example.local", "10.1.1.5", "SRV01", "bad"]);
        assert_eq!(p.duplicates, 1);
        assert!(p.invalid.contains(&"999.1.1.1".to_string()));
        assert!(p.invalid.contains(&"-x".to_string()));
        assert!(p.invalid.contains(&"host!".to_string()));
    }

    #[test]
    fn ipv6_and_empty() {
        assert_eq!(parse("").valid.len(), 0);
        assert_eq!(parse("fe80::1").valid, vec!["fe80::1"]);
    }
}
