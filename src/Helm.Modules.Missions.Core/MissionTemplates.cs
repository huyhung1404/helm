namespace Helm.Modules.Missions;

/// <summary>A ready-made roadmap to start from without an AI.</summary>
public sealed record MissionTemplate(string Id, string Name, string Description, string Json);

/// <summary>
/// A few ready-made missions, in the import format: picking one opens the import preview with it, to rename, adjust
/// and create (a test checks each one imports cleanly). For anything personal, the prompt with an AI fits better.
/// </summary>
public static class MissionTemplates
{
    public static IReadOnlyList<MissionTemplate> All { get; } =
    [
        new("run-5k", "Run 5 km", "From walking to running 5 km without stopping, in about 8 weeks.", """
            {
              "helmMission": 1,
              "title": "Run 5 km",
              "goal": "Run 5 km without stopping",
              "reward": "New running shoes",
              "phases": [
                { "title": "Get moving", "reward": "A smoothie after the run", "steps": [
                  { "title": "Walk-run 20 minutes, 3 times", "description": "1 minute running, 2 minutes walking.", "doneWhen": "3 sessions done this week", "estimateDays": 7, "checklist": ["Session 1", "Session 2", "Session 3"] },
                  { "title": "Walk-run 25 minutes, 3 times", "description": "2 minutes running, 1 minute walking.", "doneWhen": "3 sessions done this week", "estimateDays": 7, "checklist": ["Session 1", "Session 2", "Session 3"] },
                  { "title": "Run 10 minutes without stopping", "doneWhen": "10 minutes of running, any pace", "estimateDays": 3 }
                ] },
                { "title": "Build up", "reward": "A new playlist", "steps": [
                  { "title": "Run 15 minutes, 3 times", "doneWhen": "3 runs of 15 minutes", "estimateDays": 7, "checklist": ["Run 1", "Run 2", "Run 3"] },
                  { "title": "Run 2 km", "doneWhen": "2 km without walking", "estimateDays": 4 },
                  { "title": "Run 20 minutes, 3 times", "doneWhen": "3 runs of 20 minutes", "estimateDays": 7, "checklist": ["Run 1", "Run 2", "Run 3"] },
                  { "title": "Run 3 km", "doneWhen": "3 km without walking", "estimateDays": 4 }
                ] },
                { "title": "5 km", "steps": [
                  { "title": "Run 25 minutes, 3 times", "doneWhen": "3 runs of 25 minutes", "estimateDays": 7, "checklist": ["Run 1", "Run 2", "Run 3"] },
                  { "title": "Run 4 km", "doneWhen": "4 km without walking", "estimateDays": 4 },
                  { "title": "Rest, then run 5 km", "doneWhen": "5 km without stopping", "estimateDays": 3 }
                ] }
              ]
            }
            """),
        new("read-books", "Read 12 books", "One book a month for a year, with a short note on each.", """
            {
              "helmMission": 1,
              "title": "Read 12 books this year",
              "goal": "12 books read, each with a short note",
              "reward": "A weekend away with a new book",
              "phases": [
                { "title": "Get the habit", "reward": "A new bookmark", "steps": [
                  { "title": "Pick the first 3 books", "doneWhen": "3 books on the list, the first one at hand", "estimateDays": 1 },
                  { "title": "Read 20 minutes a day for a week", "doneWhen": "7 days in a row", "estimateDays": 7 },
                  { "title": "Finish book 1 and write 5 lines about it", "doneWhen": "A note with what stayed with you", "estimateDays": 21 }
                ] },
                { "title": "Books 2 to 6", "reward": "A trip to a bookshop", "steps": [
                  { "title": "Finish book 2", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 3", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 4", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 5", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 6 and look back at the half year", "doneWhen": "Read; your 3 favourite ideas so far written down", "estimateDays": 30 }
                ] },
                { "title": "Books 7 to 12", "steps": [
                  { "title": "Finish book 7", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 8", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 9", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 10", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 11", "doneWhen": "Read, with a 5-line note", "estimateDays": 30 },
                  { "title": "Finish book 12 and pick next year's first book", "doneWhen": "Read, and the year's best book chosen", "estimateDays": 30 }
                ] }
              ]
            }
            """),
        new("language-a1", "Language basics (A1)", "The first 500 words, simple sentences and a short conversation, in about 3 months.", """
            {
              "helmMission": 1,
              "title": "Reach A1 in a new language",
              "goal": "Introduce yourself, ask simple questions and know about 500 words",
              "reward": "A meal at a restaurant from that country",
              "phases": [
                { "title": "Sounds and first words", "reward": "A film in that language", "steps": [
                  { "title": "Learn the alphabet and the sounds", "doneWhen": "Read 20 new words aloud correctly", "estimateDays": 5 },
                  { "title": "Learn 100 everyday words", "description": "A flashcard app, 15 new words a day.", "doneWhen": "90% right on a self-test", "estimateDays": 7 },
                  { "title": "Numbers, days and time", "doneWhen": "Say today's date and the time without notes", "estimateDays": 4 },
                  { "title": "Review week", "doneWhen": "All cards reviewed, none overdue", "estimateDays": 3 }
                ] },
                { "title": "Simple sentences", "reward": "A new notebook", "steps": [
                  { "title": "Introduce yourself", "doneWhen": "A 1-minute self-introduction recorded", "estimateDays": 5 },
                  { "title": "Learn 150 more words", "doneWhen": "90% right on a self-test", "estimateDays": 10 },
                  { "title": "Present tense of the 20 most common verbs", "doneWhen": "Write 20 sentences, one per verb", "estimateDays": 7 },
                  { "title": "Questions: who, what, where, when, how much", "doneWhen": "Ask and answer 10 questions aloud", "estimateDays": 5 }
                ] },
                { "title": "Talk", "steps": [
                  { "title": "Learn 250 more words", "doneWhen": "500 words known in all", "estimateDays": 14 },
                  { "title": "Shop, order food, ask the way", "doneWhen": "Act out the three scenes aloud", "estimateDays": 7 },
                  { "title": "A 10-minute conversation", "description": "With a tutor, a language partner or a friend.", "doneWhen": "10 minutes spoken, mostly in the language", "estimateDays": 7 },
                  { "title": "A1 practice test", "doneWhen": "Pass a free online A1 test", "estimateDays": 3 }
                ] }
              ]
            }
            """),
        new("habit-30", "A 30-day habit", "Make one small daily habit stick: start tiny, then grow it.", """
            {
              "helmMission": 1,
              "title": "A habit in 30 days",
              "goal": "Do the habit 30 days out of 30",
              "reward": "Something that makes the habit nicer",
              "phases": [
                { "title": "Start tiny", "reward": "A small treat", "steps": [
                  { "title": "Choose the habit and its 2-minute version", "doneWhen": "One sentence: after [cue], I will [habit]", "estimateDays": 1 },
                  { "title": "Days 1-7: the 2-minute version every day", "doneWhen": "7 days in a row", "estimateDays": 7, "checklist": ["Day 1", "Day 2", "Day 3", "Day 4", "Day 5", "Day 6", "Day 7"] }
                ] },
                { "title": "Grow it", "reward": "A bigger treat", "steps": [
                  { "title": "Days 8-14: a little more each day", "doneWhen": "7 days in a row", "estimateDays": 7 },
                  { "title": "Days 15-21: the full habit", "doneWhen": "7 days in a row", "estimateDays": 7 },
                  { "title": "Plan for the hard days", "doneWhen": "Write what you will do on a busy or tired day", "estimateDays": 1 }
                ] },
                { "title": "Make it stick", "steps": [
                  { "title": "Days 22-30: the full habit", "doneWhen": "9 more days, 30 in all", "estimateDays": 9 },
                  { "title": "Look back", "doneWhen": "A note: what helped, what to keep", "estimateDays": 1 }
                ] }
              ]
            }
            """),
        new("side-project", "Ship a side project", "From an idea to a first version people can use, in about 6 weeks.", """
            {
              "helmMission": 1,
              "title": "Ship a side project",
              "goal": "A first version online that someone else has used",
              "reward": "A dinner to celebrate the launch",
              "phases": [
                { "title": "Shape it", "reward": "A coffee at a nice place", "steps": [
                  { "title": "Write the one-sentence pitch", "doneWhen": "Who it is for and what it does, in one sentence", "estimateDays": 1 },
                  { "title": "List the 3 features of the first version", "doneWhen": "Everything else on a later list", "estimateDays": 1 },
                  { "title": "Talk to 3 people who might use it", "doneWhen": "3 conversations, notes written", "estimateDays": 5 },
                  { "title": "Sketch the main screens", "doneWhen": "Sketches of every screen of the 3 features", "estimateDays": 2 }
                ] },
                { "title": "Build", "reward": "A day off", "steps": [
                  { "title": "Set up the project and deploy a hello world", "doneWhen": "It runs online", "estimateDays": 2 },
                  { "title": "Build feature 1", "doneWhen": "Works end to end", "estimateDays": 5 },
                  { "title": "Build feature 2", "doneWhen": "Works end to end", "estimateDays": 5 },
                  { "title": "Build feature 3", "doneWhen": "Works end to end", "estimateDays": 5 },
                  { "title": "Fix what a friend trips over", "doneWhen": "A friend uses it without help", "estimateDays": 3 }
                ] },
                { "title": "Launch", "steps": [
                  { "title": "Write the landing page", "doneWhen": "The pitch, a screenshot and a way to start", "estimateDays": 2 },
                  { "title": "Share it in 3 places", "doneWhen": "Posted where your people are", "estimateDays": 2 },
                  { "title": "Collect the first feedback", "doneWhen": "5 pieces of feedback written down", "estimateDays": 7 }
                ] }
              ]
            }
            """),
    ];
}
