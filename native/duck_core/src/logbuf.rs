//! Log in memoria: un ring buffer globale + uno per task (la finestra "log del task").

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet, VecDeque};

pub const PER_TASK: usize = 400;
pub const GLOBAL: usize = 5000;

#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct LogEntry {
    pub seq: u64,
    pub ts: i64,
    pub level: String,
    pub task: Option<String>,
    pub msg: String,
}

pub struct LogStore {
    seq: u64,
    global: VecDeque<LogEntry>,
    per_task: HashMap<String, VecDeque<LogEntry>>,
}

fn push_capped(q: &mut VecDeque<LogEntry>, e: LogEntry, cap: usize) {
    if q.len() >= cap {
        q.pop_front();
    }
    q.push_back(e);
}

impl LogStore {
    pub fn new() -> Self {
        LogStore { seq: 0, global: VecDeque::new(), per_task: HashMap::new() }
    }

    pub fn append(&mut self, task: Option<&str>, level: &str, msg: &str, ts: i64) -> u64 {
        self.seq += 1;
        let e = LogEntry { seq: self.seq, ts, level: level.to_string(), task: task.map(str::to_string), msg: msg.to_string() };
        if let Some(id) = task {
            push_capped(self.per_task.entry(id.to_string()).or_default(), e.clone(), PER_TASK);
        }
        push_capped(&mut self.global, e, GLOBAL);
        self.seq
    }

    /// Voci con `seq > after`, al piu' `limit` (le piu' recenti se ce ne sono di piu').
    pub fn get(&self, task: Option<&str>, after: u64, limit: usize) -> Vec<LogEntry> {
        let src = match task {
            Some(id) => match self.per_task.get(id) {
                Some(q) => q,
                None => return Vec::new(),
            },
            None => &self.global,
        };
        let mut v: Vec<LogEntry> = src.iter().filter(|e| e.seq > after).cloned().collect();
        if v.len() > limit {
            v.drain(0..v.len() - limit);
        }
        v
    }

    pub fn clear_global(&mut self) {
        self.global.clear();
    }

    pub fn retain_tasks(&mut self, alive: &HashSet<String>) {
        self.per_task.retain(|k, _| alive.contains(k));
    }

    pub fn export_tasks(&self) -> HashMap<String, Vec<LogEntry>> {
        self.per_task.iter().map(|(k, v)| (k.clone(), v.iter().cloned().collect())).collect()
    }

    pub fn import_tasks(&mut self, logs: HashMap<String, Vec<LogEntry>>) {
        for (k, v) in logs {
            for e in &v {
                self.seq = self.seq.max(e.seq);
            }
            let mut q: VecDeque<LogEntry> = v.into_iter().collect();
            while q.len() > PER_TASK {
                q.pop_front();
            }
            self.per_task.insert(k, q);
        }
    }
}
