#[derive(Debug, Clone)]
pub struct TextEditor {
    lines: Vec<String>,
    row: usize,
    column: usize,
}

impl TextEditor {
    pub fn new(text: &str) -> Self {
        let mut lines: Vec<String> = text.split('\n').map(str::to_owned).collect();
        if lines.is_empty() {
            lines.push(String::new());
        }
        Self { lines, row: 0, column: 0 }
    }

    pub fn text(&self) -> String {
        self.lines.join("\n")
    }

    pub fn lines(&self) -> &[String] {
        &self.lines
    }

    pub fn cursor(&self) -> (usize, usize) {
        (self.row, self.column)
    }

    pub fn insert(&mut self, character: char) {
        self.lines[self.row].insert(self.column, character);
        self.column += character.len_utf8();
    }

    pub fn newline(&mut self) {
        let remainder = self.lines[self.row].split_off(self.column);
        self.lines.insert(self.row + 1, remainder);
        self.row += 1;
        self.column = 0;
    }

    pub fn backspace(&mut self) {
        if self.column > 0 {
            let previous = self.lines[self.row][..self.column].chars().next_back();
            if let Some(character) = previous {
                let start = self.column - character.len_utf8();
                self.lines[self.row].drain(start..self.column);
                self.column = start;
            }
        } else if self.row > 0 {
            let current = self.lines.remove(self.row);
            self.row -= 1;
            self.column = self.lines[self.row].len();
            self.lines[self.row].push_str(&current);
        }
    }

    pub fn move_left(&mut self) {
        if self.column > 0 {
            self.column -= self.lines[self.row][..self.column].chars().next_back().map_or(1, char::len_utf8);
        } else if self.row > 0 {
            self.row -= 1;
            self.column = self.lines[self.row].len();
        }
    }

    pub fn move_right(&mut self) {
        if self.column < self.lines[self.row].len() {
            self.column += self.lines[self.row][self.column..].chars().next().map_or(1, char::len_utf8);
        } else if self.row + 1 < self.lines.len() {
            self.row += 1;
            self.column = 0;
        }
    }

    pub fn move_up(&mut self) {
        if self.row > 0 {
            self.row -= 1;
            self.column = self.column.min(self.lines[self.row].len());
        }
    }

    pub fn move_down(&mut self) {
        if self.row + 1 < self.lines.len() {
            self.row += 1;
            self.column = self.column.min(self.lines[self.row].len());
        }
    }

    pub fn home(&mut self) { self.column = 0; }
    pub fn end(&mut self) { self.column = self.lines[self.row].len(); }
}
