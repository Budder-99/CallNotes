use crate::calls::Call;
use rusqlite::{params, Connection, Result};

pub struct Storage {
    connection: Connection,
}

impl Storage {
    pub fn open() -> Result<Self> {
        let connection = Connection::open("callnotes.db")?;
        connection.execute_batch(
            "CREATE TABLE IF NOT EXISTS calls (
                id INTEGER PRIMARY KEY,
                phone TEXT NOT NULL,
                inc TEXT NOT NULL,
                call_type TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                notes TEXT NOT NULL
            );",
        )?;
        Ok(Self { connection })
    }

    pub fn load_calls(&self) -> Result<Vec<Call>> {
        let mut statement = self.connection.prepare(
            "SELECT id, phone, inc, call_type, started_at, finished_at, notes FROM calls ORDER BY id DESC",
        )?;
        let calls = statement.query_map([], |row| Ok(Call {
            id: Some(row.get(0)?), phone: row.get(1)?, inc: row.get(2)?,
            call_type: row.get(3)?, started_at: row.get(4)?, finished_at: row.get(5)?, notes: row.get(6)?,
        }))?.collect::<Result<Vec<_>>>()?;
        Ok(calls)
    }

    pub fn save_call(&self, call: &mut Call) -> Result<()> {
        if let Some(id) = call.id {
            self.connection.execute(
                "UPDATE calls SET phone = ?1, inc = ?2, call_type = ?3, started_at = ?4, finished_at = ?5, notes = ?6 WHERE id = ?7",
                params![call.phone, call.inc, call.call_type, call.started_at, call.finished_at, call.notes, id],
            )?;
        } else {
            self.connection.execute(
                "INSERT INTO calls (phone, inc, call_type, started_at, finished_at, notes) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                params![call.phone, call.inc, call.call_type, call.started_at, call.finished_at, call.notes],
            )?;
            call.id = Some(self.connection.last_insert_rowid());
        }
        Ok(())
    }
}
