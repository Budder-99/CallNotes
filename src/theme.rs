use ratatui::style::{Color, Modifier, Style};

#[derive(Clone, Copy)]
pub struct Theme {
    pub background: Color,
    pub foreground: Color,
    pub muted: Color,
    pub accent: Color,
    pub active: Color,
    pub border: Color,
}

impl Default for Theme {
    fn default() -> Self {
        Self {
            background: Color::Rgb(18, 20, 24),
            foreground: Color::Rgb(225, 229, 235),
            muted: Color::Rgb(130, 140, 153),
            accent: Color::Rgb(92, 190, 170),
            active: Color::Rgb(255, 193, 92),
            border: Color::Rgb(65, 76, 88),
        }
    }
}

impl Theme {
    pub fn title(self) -> Style { Style::default().fg(self.accent).add_modifier(Modifier::BOLD) }
    pub fn muted(self) -> Style { Style::default().fg(self.muted) }
    pub fn active(self) -> Style { Style::default().fg(self.active).add_modifier(Modifier::BOLD) }
}
