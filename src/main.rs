mod app;
mod calls;
mod editor;
mod input;
mod storage;
mod theme;

use app::App;
use crossterm::{execute, terminal::{disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen}};
use ratatui::{backend::CrosstermBackend, Terminal};
use std::io;
use storage::Storage;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let storage = Storage::open()?;
    let mut app = App::new(&storage);
    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen)?;
    let backend = CrosstermBackend::new(stdout);
    let mut terminal = Terminal::new(backend)?;
    let result = run(&mut terminal, &mut app, &storage);
    disable_raw_mode()?;
    execute!(terminal.backend_mut(), LeaveAlternateScreen)?;
    terminal.show_cursor()?;
    result
}

fn run<B: ratatui::backend::Backend>(terminal: &mut Terminal<B>, app: &mut App, storage: &Storage) -> Result<(), Box<dyn std::error::Error>> {
    while !app.should_quit {
        terminal.draw(|frame| app.render(frame))?;
        input::handle_events(app, storage)?;
    }
    Ok(())
}
