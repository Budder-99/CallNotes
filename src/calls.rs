use chrono::Local;

#[derive(Debug, Clone)]
pub struct Call {
    pub id: Option<i64>,
    pub phone: String,
    pub inc: String,
    pub call_type: String,
    pub started_at: String,
    pub finished_at: Option<String>,
    pub notes: String,
}

impl Call {
    pub fn new() -> Self {
        Self {
            id: None,
            phone: String::new(),
            inc: String::new(),
            call_type: String::new(),
            started_at: Local::now().format("%Y-%m-%d %H:%M:%S").to_string(),
            finished_at: None,
            notes: String::new(),
        }
    }

    pub fn finish(&mut self) {
        if self.finished_at.is_none() {
            self.finished_at = Some(Local::now().format("%Y-%m-%d %H:%M:%S").to_string());
        }
    }

    pub fn title(&self) -> String {
        let label = if self.inc.is_empty() { "New call" } else { &self.inc };
        format!(" {} | {} ", label, if self.finished_at.is_some() { "finished" } else { "active" })
    }
}
