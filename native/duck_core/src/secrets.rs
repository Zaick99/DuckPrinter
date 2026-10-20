//! Segreti di sessione (es. password AD): vivono qui, non in stringhe gestite a lunga vita.
//! Il buffer viene azzerato con scritture volatili alla rimozione e al drop.

use std::collections::HashMap;

pub struct SecretBuf(Vec<u8>);

impl SecretBuf {
    fn new(s: &str) -> Self {
        SecretBuf(s.as_bytes().to_vec())
    }
    fn as_str(&self) -> &str {
        std::str::from_utf8(&self.0).unwrap_or("")
    }
}

impl Drop for SecretBuf {
    fn drop(&mut self) {
        for b in self.0.iter_mut() {
            // SAFETY: puntatore valido derivato da un riferimento mutabile
            unsafe { std::ptr::write_volatile(b, 0) };
        }
        std::sync::atomic::compiler_fence(std::sync::atomic::Ordering::SeqCst);
    }
}

#[derive(Default)]
pub struct SecretStore(HashMap<String, SecretBuf>);

impl SecretStore {
    pub fn put(&mut self, name: &str, value: &str) {
        self.0.insert(name.to_string(), SecretBuf::new(value)); // il vecchio valore viene azzerato dal drop
    }
    pub fn get(&self, name: &str) -> Option<&str> {
        self.0.get(name).map(SecretBuf::as_str)
    }
    pub fn drop_one(&mut self, name: &str) -> bool {
        self.0.remove(name).is_some()
    }
    pub fn clear(&mut self) {
        self.0.clear();
    }
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn put_get_drop() {
        let mut s = SecretStore::default();
        s.put("ad", "p@ss");
        assert_eq!(s.get("ad"), Some("p@ss"));
        s.put("ad", "nuova");
        assert_eq!(s.get("ad"), Some("nuova"));
        assert!(s.drop_one("ad"));
        assert!(s.get("ad").is_none());
        s.put("x", "y");
        s.clear();
        assert!(s.is_empty());
    }
}
