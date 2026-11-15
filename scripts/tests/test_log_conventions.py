"""Checks every log call in the plugin against the logging rules in AGENTS.md (Coding Style)."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).parents[2]
SOURCE = ROOT / 'jellyfin-anidoki'
CALL = re.compile(r'\.Log(Trace|Debug|Information|Warning|Error|Critical)\(')
PLACEHOLDER = re.compile(r'\{(\w+)[^{}]*\}')
LEAD = re.compile(r'\(([^)]*)\) ')
# Placeholder names that were replaced, and the one name each thing has now.
RENAMED = {
    'ApiName': 'Provider',
    'JellyfinUserId': 'UserId',
    'BaseItemName': 'ItemName',
    'VideoName': 'ItemName',
    'SeasonIndexNumber': 'SeasonNumber',
    'EpisodeIndexNumber': 'EpisodeNumber',
}
# Text that belongs in the exception argument, not in a placeholder.
EXCEPTION_TEXT = {'Exception', 'StackTrace'}


def log_calls(text):
    """Yields (line number, level, argument text) for every .LogLevel(...) call."""
    for match in CALL.finditer(text):
        depth, index, in_string = 1, match.end(), False
        while index < len(text) and depth:
            char = text[index]
            if in_string:
                if char == '\\':
                    index += 1
                elif char == '"':
                    in_string = False
            elif char == '"':
                in_string = True
            elif char == '(':
                depth += 1
            elif char == ')':
                depth -= 1
            index += 1
        yield text.count('\n', 0, match.start()) + 1, match.group(1), text[match.end():index - 1]


def split_arguments(arguments):
    """Splits on the commas that separate arguments, not those inside strings or brackets."""
    parts, current, depth, in_string, index = [], [], 0, False, 0
    while index < len(arguments):
        char = arguments[index]
        if in_string:
            current.append(char)
            if char == '\\':
                index += 1
                current.append(arguments[index])
            elif char == '"':
                in_string = False
        elif char == '"':
            in_string = True
            current.append(char)
        elif char in '([{':
            depth += 1
            current.append(char)
        elif char in ')]}':
            depth -= 1
            current.append(char)
        elif char == ',' and depth == 0:
            parts.append(''.join(current).strip())
            current = []
        else:
            current.append(char)
        index += 1
    parts.append(''.join(current).strip())
    return parts


def template_of(arguments):
    """The message template: the first of the first two arguments that is a string literal."""
    for argument in split_arguments(arguments)[:2]:
        if len(argument) >= 2 and argument.startswith('"') and argument.endswith('"'):
            return argument[1:-1]
    return None


def violations(level, arguments):
    template = template_of(arguments)
    if template is None:
        return []
    found = []
    lead = LEAD.match(template)
    if lead and lead.group(1) != '{Provider}':
        found.append(f'starts with ({lead.group(1)}); only ({{Provider}}) may lead a message')
    if template.startswith('['):
        found.append('starts with a [tag]')
    if '...' in template:
        found.append('contains "..."')
    elif template.rstrip().endswith(('.', '!')):
        found.append('ends with punctuation')
    names = PLACEHOLDER.findall(template)
    for name in names:
        if name in RENAMED:
            found.append(f'uses {{{name}}}; the name is {{{RENAMED[name]}}}')
        if name in EXCEPTION_TEXT:
            found.append(f'uses {{{name}}}; pass the exception as the first argument')
    if level in ('Error', 'Critical') and 'ErrorMessage' in names:
        found.append('logs {ErrorMessage} at Error; pass the exception as the first argument')
    return found


def scan(source):
    found = []
    for path in sorted(source.rglob('*.cs')):
        if {'bin', 'obj'} & set(path.relative_to(source).parts):
            continue
        for line, level, arguments in log_calls(path.read_text(encoding='utf-8')):
            for violation in violations(level, arguments):
                found.append(f'{path.relative_to(source.parent)}:{line}: {violation}')
    return found


class LogConventionRuleTests(unittest.TestCase):
    def test_a_message_that_follows_the_rules_passes(self):
        self.assertEqual(violations('Information', '"({Provider}) {Title}: {Kind} ({Step})", tracker.Name, title, kind, step'), [])
        self.assertEqual(violations('Error', 'error, "Could not read anime {AnimeId}", animeId'), [])
        self.assertEqual(violations('Warning', '"Could not record progress: {ErrorMessage}", error.Message'), [])

    def test_a_literal_source_prefix_fails(self):
        self.assertEqual(violations('Debug', '"(Sync) Waiting"'), ['starts with (Sync); only ({Provider}) may lead a message'])
        self.assertEqual(len(violations('Debug', '"(Kitsu) Reading the user"')), 1)

    def test_a_lead_other_than_provider_fails(self):
        self.assertEqual(len(violations('Debug', '"({AniDbKey}) Found {ItemName}", key, name')), 1)

    def test_a_tag_fails(self):
        self.assertEqual(violations('Debug', '"[anidoki-diag] Series {SeriesName}", name'), ['starts with a [tag]'])

    def test_trailing_punctuation_fails(self):
        self.assertEqual(violations('Debug', '"Fetching the list..."'), ['contains "..."'])
        self.assertEqual(violations('Warning', '"Could not remove the page."'), ['ends with punctuation'])
        self.assertEqual(violations('Information', '"Too many requests!"'), ['ends with punctuation'])

    def test_an_old_placeholder_name_fails(self):
        self.assertEqual(violations('Debug', '"Using {ApiName}", name'), ['uses {ApiName}; the name is {Provider}'])

    def test_an_error_that_logs_only_the_message_fails(self):
        self.assertEqual(len(violations('Error', '"Could not sync: {ErrorMessage}", e.Message')), 1)
        self.assertEqual(len(violations('Error', '"Failed: {Message}\\n{StackTrace}", e.Message, e.StackTrace')), 1)
        self.assertEqual(len(violations('Error', '"Fatal error: {Exception}", exception')), 1)

    def test_commas_and_brackets_inside_arguments_do_not_split_them(self):
        self.assertEqual(split_arguments('error, "A, b ({C})", Join(", ", list), x ? "y" : "z"'),
                         ['error', '"A, b ({C})"', 'Join(", ", list)', 'x ? "y" : "z"'])
        self.assertEqual(template_of('error, "Could not read {Path}", path'), 'Could not read {Path}')
        self.assertIsNone(template_of('message'))


class RepositoryLogTests(unittest.TestCase):
    def test_every_log_call_follows_the_conventions(self):
        found = scan(SOURCE)
        self.assertEqual(found, [], f'{len(found)} log calls break the conventions:\n' + '\n'.join(found))


if __name__ == '__main__':
    unittest.main()
