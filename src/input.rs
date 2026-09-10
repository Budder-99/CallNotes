use crate::{app::App, storage::Storage};
use crossterm::event::{self, Event};
use std::io;

pub fn handle_events(app: &mut App, storage: &Storage) -> io::Result<()> {
    if event::poll(std::time::Duration::from_millis(100))? {
        if let Event::Key(key) = event::read()? { app.handle_key(key, storage); }
    }
    Ok(())
}
