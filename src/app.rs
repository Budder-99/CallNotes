use crate::{calls::Call, editor::TextEditor, storage::Storage, theme::Theme};
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::{layout::{Constraint, Direction, Layout, Rect}, style::{Color, Style}, text::{Line, Span, Text}, widgets::{Block, Borders, Paragraph, Wrap}, Frame};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Field { Phone, Inc, CallType, Notes }

pub struct App {
    pub calls: Vec<Call>,
    pub selected: usize,
    pub field: Field,
    pub editor: TextEditor,
    pub scroll: u16,
    pub status: String,
    pub should_quit: bool,
    pub theme: Theme,
}

impl App {
    pub fn new(storage: &Storage) -> Self {
        let mut calls = storage.load_calls().unwrap_or_default();
        if calls.is_empty() { calls.push(Call::new()); }
        let selected = 0;
        let editor = TextEditor::new(&calls[selected].notes);
        Self { calls, selected, field: Field::Notes, editor, scroll: 0, status: "Ready".into(), should_quit: false, theme: Theme::default() }
    }

    fn current(&mut self) -> &mut Call { &mut self.calls[self.selected] }

    fn sync_editor(&mut self) {
        self.current().notes = self.editor.text();
    }

    fn select(&mut self, index: usize) {
        self.sync_editor();
        self.selected = index.min(self.calls.len().saturating_sub(1));
        self.editor = TextEditor::new(&self.calls[self.selected].notes);
        self.field = Field::Notes;
    }

    pub fn handle_key(&mut self, key: KeyEvent, storage: &Storage) {
        if key.modifiers.contains(KeyModifiers::CONTROL) {
            match key.code {
                KeyCode::Char('q') => { let _ = self.save(storage); self.should_quit = true; return; }
                KeyCode::Char('s') => { self.save(storage); return; }
                KeyCode::Char('n') => { self.sync_editor(); self.calls.insert(0, Call::new()); self.selected = 0; self.editor = TextEditor::new(""); self.field = Field::Notes; self.status = "New call started".into(); return; }
                KeyCode::Char('1') => { self.insert_macro("BMC Engineer"); return; }
                KeyCode::Char('2') => { self.insert_macro("Coforge"); return; }
                KeyCode::Char('3') => { self.insert_macro("INC-"); return; }
                KeyCode::Up => { self.select(self.selected.saturating_sub(1)); return; }
                KeyCode::Down => { self.select((self.selected + 1).min(self.calls.len().saturating_sub(1))); return; }
                _ => {}
            }
        }
        match key.code {
            KeyCode::F(2) => { self.sync_editor(); self.current().finish(); self.save(storage); self.status = "Call finished and saved".into(); }
            KeyCode::Tab => self.next_field(false),
            KeyCode::BackTab => self.next_field(true),
            KeyCode::PageUp => self.scroll = self.scroll.saturating_sub(8),
            KeyCode::PageDown => self.scroll = self.scroll.saturating_add(8),
            KeyCode::Up if self.field == Field::Notes => self.editor.move_up(),
            KeyCode::Down if self.field == Field::Notes => self.editor.move_down(),
            KeyCode::Left if self.field == Field::Notes => self.editor.move_left(),
            KeyCode::Right if self.field == Field::Notes => self.editor.move_right(),
            KeyCode::Home if self.field == Field::Notes => self.editor.home(),
            KeyCode::End if self.field == Field::Notes => self.editor.end(),
            KeyCode::Enter if self.field == Field::Notes => self.editor.newline(),
            KeyCode::Backspace if self.field == Field::Notes => self.editor.backspace(),
            KeyCode::Backspace => self.remove_last_char(),
            KeyCode::Char(character) => self.insert_char(character),
            _ => {}
        }
        self.sync_editor();
    }

    fn next_field(&mut self, reverse: bool) {
        self.sync_editor();
        self.field = match (self.field, reverse) {
            (Field::Phone, false) => Field::Inc, (Field::Inc, false) => Field::CallType, (Field::CallType, false) => Field::Notes, (Field::Notes, false) => Field::Phone,
            (Field::Phone, true) => Field::Notes, (Field::Inc, true) => Field::Phone, (Field::CallType, true) => Field::Inc, (Field::Notes, true) => Field::CallType,
        };
        self.editor = TextEditor::new(&self.current().notes);
    }

    fn insert_char(&mut self, character: char) {
        match self.field {
            Field::Notes => self.editor.insert(character),
            Field::Phone => self.current().phone.push(character),
            Field::Inc => self.current().inc.push(character),
            Field::CallType => self.current().call_type.push(character),
        }
    }

    fn remove_last_char(&mut self) {
        let value = match self.field {
            Field::Phone => &mut self.current().phone,
            Field::Inc => &mut self.current().inc,
            Field::CallType => &mut self.current().call_type,
            Field::Notes => return,
        };
        value.pop();
    }

    fn insert_macro(&mut self, value: &str) {
        match self.field { Field::Notes => value.chars().for_each(|c| self.editor.insert(c)), _ => value.chars().for_each(|c| self.insert_char(c)) }
        self.sync_editor();
    }

    fn save(&mut self, storage: &Storage) {
        self.sync_editor();
        match storage.save_call(self.current()) { Ok(()) => self.status = "Saved".into(), Err(error) => self.status = format!("Save failed: {error}"), }
    }

    pub fn render(&self, frame: &mut Frame) {
        let area = frame.area();
        let chunks = Layout::default().direction(Direction::Vertical).constraints([Constraint::Length(2), Constraint::Min(1), Constraint::Length(2)]).split(area);
        let header = Paragraph::new(Line::from(vec![Span::styled(" CALLNOTES ", self.theme.title()), Span::styled("local service desk log", self.theme.muted())])).style(Style::default().bg(self.theme.background));
        frame.render_widget(header, chunks[0]);
        self.render_calls(frame, chunks[1]);
        let footer = Paragraph::new(Line::from(vec![Span::styled(format!(" {} ", self.status), self.theme.active()), Span::styled("  Ctrl+N new  Ctrl+S save  F2 finish  Ctrl+Q quit  Tab fields", self.theme.muted())])).style(Style::default().bg(self.theme.background));
        frame.render_widget(footer, chunks[2]);
    }

    fn render_calls(&self, frame: &mut Frame, area: Rect) {
        let mut y = area.y.saturating_sub(self.scroll);
        for (index, call) in self.calls.iter().enumerate() {
            let note_lines = call.notes.lines().count().max(1);
            let height = (6 + note_lines).min(u16::MAX as usize) as u16;
            if y + height > area.y && y < area.y + area.height {
                let rect = Rect { x: area.x, y, width: area.width, height };
                let border = if index == self.selected { self.theme.active } else { self.theme.border };
                let title_style = if index == self.selected { self.theme.active() } else { self.theme.title() };
                let mut lines = vec![
                    Line::from(vec![Span::styled("Phone ", self.theme.muted()), Span::raw(&call.phone), Span::styled("    INC ", self.theme.muted()), Span::raw(&call.inc)]),
                    Line::from(vec![Span::styled("Type  ", self.theme.muted()), Span::raw(&call.call_type)]),
                    Line::from(vec![Span::styled("Start ", self.theme.muted()), Span::raw(&call.started_at), Span::styled("    End ", self.theme.muted()), Span::raw(call.finished_at.as_deref().unwrap_or("-"))]),
                    Line::from(Span::styled("Notes", self.theme.muted())),
                ];
                lines.extend(Text::from(call.notes.clone()).lines.into_iter().map(|line| line.style(if index == self.selected { Style::default().fg(self.theme.foreground) } else { self.theme.muted() })));
                let block = Block::default().borders(Borders::ALL).border_style(Style::default().fg(border)).title(Span::styled(call.title(), title_style));
                frame.render_widget(Paragraph::new(lines).block(block).wrap(Wrap { trim: false }).style(Style::default().bg(self.theme.background)), rect);
            }
            y = y.saturating_add(height + 1);
        }
    }
}
