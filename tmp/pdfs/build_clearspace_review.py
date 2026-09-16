from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import (
    BaseDocTemplate,
    Frame,
    KeepTogether,
    NextPageTemplate,
    PageBreak,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
)


ROOT = Path(r"E:\GithubRepos\File Explorer Remake")
OUTPUT = ROOT / "output" / "pdf" / "Clearspace_CS499_Code_Review_Outline.pdf"


NAVY = colors.HexColor("#17324D")
BLUE = colors.HexColor("#2F6B9A")
TEAL = colors.HexColor("#2E7D75")
INK = colors.HexColor("#22313F")
MUTED = colors.HexColor("#617181")
PALE = colors.HexColor("#EAF2F7")
LINE = colors.HexColor("#D5E0E8")


styles = getSampleStyleSheet()
styles.add(ParagraphStyle(
    name="CoverTitle", parent=styles["Title"], fontName="Helvetica-Bold",
    fontSize=29, leading=34, textColor=NAVY, alignment=TA_CENTER, spaceAfter=14,
))
styles.add(ParagraphStyle(
    name="CoverSubtitle", parent=styles["Normal"], fontName="Helvetica",
    fontSize=14, leading=20, textColor=MUTED, alignment=TA_CENTER,
))
styles.add(ParagraphStyle(
    name="H1Clearspace", parent=styles["Heading1"], fontName="Helvetica-Bold",
    fontSize=18, leading=22, textColor=NAVY, spaceBefore=2, spaceAfter=10,
))
styles.add(ParagraphStyle(
    name="H2Clearspace", parent=styles["Heading2"], fontName="Helvetica-Bold",
    fontSize=12.5, leading=16, textColor=BLUE, spaceBefore=10, spaceAfter=5,
))
styles.add(ParagraphStyle(
    name="BodyClearspace", parent=styles["BodyText"], fontName="Helvetica",
    fontSize=9.6, leading=14.4, textColor=INK, spaceAfter=7,
))
styles.add(ParagraphStyle(
    name="BulletClearspace", parent=styles["BodyText"], fontName="Helvetica",
    fontSize=9.5, leading=13.5, textColor=INK, leftIndent=14, firstLineIndent=-8,
    spaceAfter=3,
))
styles.add(ParagraphStyle(
    name="SmallClearspace", parent=styles["BodyText"], fontName="Helvetica",
    fontSize=8.3, leading=11.5, textColor=MUTED, spaceAfter=4,
))
styles.add(ParagraphStyle(
    name="CalloutClearspace", parent=styles["BodyText"], fontName="Helvetica-Bold",
    fontSize=10.2, leading=14.5, textColor=NAVY, spaceAfter=1,
))


def p(text, style="BodyClearspace"):
    return Paragraph(text, styles[style])


def bullet(text):
    return p("- " + text, "BulletClearspace")


def header_footer(canvas, doc):
    canvas.saveState()
    width, height = letter
    canvas.setStrokeColor(LINE)
    canvas.setLineWidth(0.6)
    canvas.line(0.65 * inch, height - 0.52 * inch, width - 0.65 * inch, height - 0.52 * inch)
    canvas.setFont("Helvetica-Bold", 8)
    canvas.setFillColor(BLUE)
    canvas.drawString(0.65 * inch, height - 0.39 * inch, "CLEARSPACE | CS 499 CODE REVIEW")
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(MUTED)
    canvas.drawRightString(width - 0.65 * inch, 0.38 * inch, f"Page {doc.page}")
    canvas.restoreState()


def title_footer(canvas, doc):
    canvas.saveState()
    width, _ = letter
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(MUTED)
    canvas.drawCentredString(width / 2, 0.48 * inch, "Prepared as a video-review guide")
    canvas.restoreState()


def section_title(number, title, duration):
    return [
        p(f"Category {number}: {title}", "H1Clearspace"),
        Table([[p(f"Suggested recording time: {duration}", "CalloutClearspace")]],
              colWidths=[6.85 * inch], style=TableStyle([
                  ("BACKGROUND", (0, 0), (-1, -1), PALE),
                  ("BOX", (0, 0), (-1, -1), 0.5, LINE),
                  ("LEFTPADDING", (0, 0), (-1, -1), 10),
                  ("RIGHTPADDING", (0, 0), (-1, -1), 10),
                  ("TOPPADDING", (0, 0), (-1, -1), 7),
                  ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
              ])),
        Spacer(1, 8),
    ]


def build():
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    frame = Frame(0.65 * inch, 0.65 * inch, 7.2 * inch, 9.75 * inch, id="body")
    title_frame = Frame(0.75 * inch, 0.8 * inch, 7.0 * inch, 9.5 * inch, id="title")
    doc = BaseDocTemplate(
        str(OUTPUT), pagesize=letter,
        leftMargin=0.65 * inch, rightMargin=0.65 * inch,
        topMargin=0.7 * inch, bottomMargin=0.65 * inch,
        title="Clearspace CS 499 Code Review Speaker Script",
        author="Clearspace project review",
    )
    doc.addPageTemplates([
        PageTemplate(id="title", frames=[title_frame], onPage=title_footer),
        PageTemplate(id="body", frames=[frame], onPage=header_footer),
    ])

    story = []
    story += [Spacer(1, 1.75 * inch)]
    story += [p("Clearspace", "CoverTitle")]
    story += [p("CS 499 Code Review Speaker Script", "CoverTitle")]
    story += [Spacer(1, 0.2 * inch)]
    story += [p("A speaker-ready 30-minute walkthrough with an on-screen file guide for software engineering and design, algorithms and data structures, and databases.", "CoverSubtitle")]
    story += [Spacer(1, 1.3 * inch)]
    story += [Table([[p("PROJECT SNAPSHOT", "CalloutClearspace")],
                     [p("Clearspace is a Windows WPF file manager with file browsing, tags, media support, persistent user preferences, and layered search. It is a suitable single artifact for all three CS 499 enhancement categories.", "BodyClearspace")]],
                    colWidths=[6.55 * inch], style=TableStyle([
                        ("BACKGROUND", (0, 0), (0, 0), colors.HexColor("#DDEBF4")),
                        ("BACKGROUND", (0, 1), (0, 1), colors.HexColor("#F7FAFC")),
                        ("BOX", (0, 0), (-1, -1), 0.7, LINE),
                        ("LEFTPADDING", (0, 0), (-1, -1), 14),
                        ("RIGHTPADDING", (0, 0), (-1, -1), 14),
                        ("TOPPADDING", (0, 0), (-1, -1), 10),
                        ("BOTTOMPADDING", (0, 0), (-1, -1), 10),
                    ]))]
    story += [NextPageTemplate("body"), PageBreak()]

    story += [p("Recording plan", "H1Clearspace")]
    data = [
        [p("Segment", "SmallClearspace"), p("Time", "SmallClearspace"), p("Purpose", "SmallClearspace")],
        [p("Opening", "BodyClearspace"), p("1 min", "BodyClearspace"), p("Introduce Clearspace and why it supports all three categories.", "BodyClearspace")],
        [p("Software engineering and design", "BodyClearspace"), p("9 min", "BodyClearspace"), p("Explain architecture, critique maintainability and error handling, then present the refactoring and testing plan.", "BodyClearspace")],
        [p("Algorithms and data structures", "BodyClearspace"), p("10 min", "BodyClearspace"), p("Explain indexed search, identify completeness and scalability limits, then present the folder-size and index-health enhancements.", "BodyClearspace")],
        [p("Databases", "BodyClearspace"), p("8 min", "BodyClearspace"), p("Review JSON persistence, critique integrity and query limitations, then present a SQLite migration plan.", "BodyClearspace")],
        [p("Closing", "BodyClearspace"), p("2 min", "BodyClearspace"), p("Connect the three enhancements to reliable, professional software delivery.", "BodyClearspace")],
    ]
    story += [Table(data, colWidths=[2.05 * inch, 0.75 * inch, 4.05 * inch], repeatRows=1,
                    style=TableStyle([
                        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
                        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                        ("GRID", (0, 0), (-1, -1), 0.4, LINE),
                        ("VALIGN", (0, 0), (-1, -1), "TOP"),
                        ("LEFTPADDING", (0, 0), (-1, -1), 7),
                        ("RIGHTPADDING", (0, 0), (-1, -1), 7),
                        ("TOPPADDING", (0, 0), (-1, -1), 6),
                        ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
                        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#F7FAFC")]),
                    ]))]
    story += [Spacer(1, 16), p("Opening script - say this", "H2Clearspace")]
    story += [p("Hello. This is my code review for Clearspace, a Windows desktop file manager that I developed as a more focused replacement for File Explorer. The application supports browsing and file operations, custom folder views, file tags, media features, and layered search. I selected this artifact because it lets me demonstrate each of the three CS 499 enhancement categories in one connected application: software engineering and design, algorithms and data structures, and databases.")]
    story += [p("In this review, I will first explain what the existing application does. Then I will identify specific strengths and limitations in the current implementation. Finally, I will explain the enhancements I plan to make and how those changes demonstrate professional software-development practice. I will keep the discussion grounded in the current source code rather than describing features only at a high level.")]
    story += [p("How to use this script", "H2Clearspace"), p("Read the narration paragraphs in your own voice. Pause at each on-screen cue, scroll through the named file, and point out the relevant method or class. These demonstrations add useful evidence and naturally bring the recording close to 30 minutes.", "BodyClearspace")]
    story += [p("Open these files in this order", "H2Clearspace")]
    story += [bullet("Clearspace/ARCHITECTURE.md - introduce the application structure and design goals."),
              bullet("Clearspace/ViewModels/MainViewModel.cs - show the large coordination role, especially RunTreeSearchAsync near line 1085."),
              bullet("Clearspace/Commands and Clearspace/Services/FileOperationService.cs - explain separation of actions and file-operation feedback."),
              bullet("Clearspace/Services/FileIndex.cs - show the IndexEntry structure, parent indices, folded names, and parallel search."),
              bullet("Clearspace/Services/FileIndexBuilder.cs - show MaxDepth near line 11 and the directory-walk algorithm."),
              bullet("Clearspace/Services/FileSearchService.cs - compare the fallback traversal depth near line 14."),
              bullet("Clearspace/Services/TagService.cs - show TagData and the JSON assignment dictionary near line 43."),
              bullet("Clearspace/Services/FileIndexStore.cs and Clearspace/Services/WindowsSearchService.cs - close by contrasting local persistence with Windows Search integration.")]

    story += section_title("One", "Software Engineering and Design", "approximately 9 minutes")
    story += [p("Speaker-ready narration", "H2Clearspace")]
    story += [p("I will begin with software engineering and design. Clearspace is organized as a WPF desktop application. The interface is defined in XAML, the view-model layer manages user-interface state, models represent the application's data, and services isolate work such as file operations, search, indexing, settings, and Windows integration. This is a good starting structure because it keeps platform-specific work out of the window code.")]
    story += [p("The application also shows awareness of user experience. Folder loading and search can take time, so Clearspace uses background tasks and cancellation rather than freezing the window. Its command architecture separates user actions from menu and keyboard code. These decisions provide a maintainable foundation and make it easier to add features without concentrating all behavior in the interface.")]
    story += [p("Existing functionality", "H2Clearspace")]
    story += [p("Clearspace uses a WPF interface with a view-model layer, models, services, command actions, and native Windows integrations. The main view model coordinates navigation, folder loading, search, sidebar content, user settings, and UI state. Services handle responsibilities such as file operations, icons, search, indexing, tags, settings, and cloud-storage detection.")]
    story += [p("The project has positive design choices. Its command pattern separates user actions from the UI, services isolate Windows-specific logic, and background work helps avoid blocking the user interface.")]
    story += [p("Show on screen", "H2Clearspace"), bullet("Clearspace/ViewModels/MainViewModel.cs"), bullet("Clearspace/Commands"), bullet("Clearspace/Services"), bullet("Clearspace/ARCHITECTURE.md")]
    story += [p("As I inspect the current implementation, the main limitation is that the main view model has grown into a large coordinator. It reacts to search input, manages navigation, stores layout preferences, builds sidebar entries, updates status text, and controls tag-related user-interface state. A class with so many responsibilities is difficult to change safely and difficult to test in focused ways.")]
    story += [p("Analysis findings", "H2Clearspace")]
    story += [bullet("MainViewModel.cs is over 2,000 lines and owns unrelated responsibilities: navigation, search, layout preferences, sidebar categories, tags, and status reporting. This makes change and testing harder."),
              bullet("Static service access tightly couples the view model to concrete implementations and prevents easy use of test doubles."),
              bullet("Several services catch broad exceptions and suppress useful failure information. For example, file operations return only a Boolean result, so the user cannot distinguish cancellation, access denied, or another Windows error."),
              bullet("No automated test project is present. That creates regression risk for search, metadata, and destructive file operations.")]
    story += [p("Planned enhancement", "H2Clearspace")]
    story += [p("My software-engineering enhancement will refactor this design into smaller, testable pieces. I will separate navigation, search coordination, and sidebar management behind focused interfaces. The main view model will still connect those parts to the user interface, but it will no longer contain every implementation detail. I will replace simple Boolean file-operation results with objects that distinguish success, user cancellation, access denied, and other Windows errors. Finally, I will add unit tests for search parsing, tag behavior, indexing edge cases, and file-operation results. These changes demonstrate maintainability, clear communication of failures, professional testing, and a security mindset around user files.")]
    story += [p("Outcome connection", "H2Clearspace"), p("This enhancement supports maintainability, clear communication of failures, professional testing practices, and secure handling of user data.")]

    story += section_title("Two", "Algorithms and Data Structures", "approximately 10 minutes")
    story += [p("Speaker-ready narration", "H2Clearspace")]
    story += [p("For the algorithms and data-structures category, Clearspace uses two complementary search paths. When a user needs results immediately, the program can traverse the file system in background workers. For repeated search, it builds an in-memory index. This design matters because it avoids requiring a full disk walk every time a user types a query.")]
    story += [p("The index makes a deliberate memory tradeoff. Rather than creating a rich object for every file, it stores index entries in arrays and stores names in a shared character pool. Every entry points to its parent by index, so a full path is reconstructed only for results that Clearspace needs to display. Names are also case-folded when they are added, then searched in parallel. This avoids repeated case conversion while the query changes.")]
    story += [p("Existing functionality", "H2Clearspace")]
    story += [p("Clearspace uses concurrent background traversal for immediate search and an in-memory index for faster repeated searches. The index stores entries in arrays and names in a shared character pool rather than creating a separate object for every file. Each entry stores a parent index, so full paths are built only for results displayed to the user.")]
    story += [p("Names are case-folded once during construction, then searched in parallel. This reduces repeated case conversion while the user types a query. A ranking service promotes useful results such as exact matches, folder names, and recent files.")]
    story += [p("Show on screen", "H2Clearspace"), bullet("Clearspace/Services/FileIndex.cs"), bullet("Clearspace/Services/FileIndexBuilder.cs"), bullet("Clearspace/Services/FileSearchService.cs"), bullet("Clearspace/Services/SearchRanker.cs")]
    story += [p("Analysis findings", "H2Clearspace")]
    story += [bullet("The index builder has a fixed traversal depth of 32, while fallback search stops at depth 24. Files nested more deeply may not be found, despite the stated goal of comprehensive search."),
              bullet("Index search is a parallel linear scan of indexed filenames. It is memory-conscious and responsive, but worst-case work still grows with the number of entries."),
              bullet("Hard-coded chunk sizes, result limits, and memory behavior are not backed by automated benchmarks or boundary tests."),
              bullet("The change overlay is global, so a watcher overflow can make the overall index unhealthy even when other volumes remain valid.")]
    story += [p("Planned enhancement", "H2Clearspace")]
    story += [p("The main issue I identified is correctness. The index builder has a fixed maximum depth of 32, and fallback search stops at a depth of 24. A deeply nested file may therefore never be indexed or discovered, which conflicts with the goal of comprehensive search. My planned algorithm enhancement is recursive folder-size aggregation. Because each indexed entry already has a file size and parent index, I can calculate folder totals with a bottom-up traversal after indexing. That avoids repeatedly walking the disk when a user asks for a folder's size. I will also remove or make the depth limit configurable, track index health per volume instead of globally, and add correctness and benchmark tests for deep trees, deletes, renames, hidden items, cancellation, and large result sets.")]
    story += [p("Outcome connection", "H2Clearspace"), p("This demonstrates time-space tradeoffs, hierarchical data structures, concurrency, performance measurement, and correctness under realistic file-system conditions.")]

    story += section_title("Three", "Databases", "approximately 8 minutes")
    story += [p("Speaker-ready narration", "H2Clearspace")]
    story += [p("For the database category, I reviewed how Clearspace persists metadata. The application lets a user create tags and assign them to files and folders. Each tag has an ID, name, and color. The current implementation stores those definitions and assignments in a JSON document. The path is a dictionary key, and the value is the list of tag IDs assigned to that path.")]
    story += [p("Clearspace also stores user preferences in JSON, persists its performance-oriented file index in a custom binary file, and can query the existing Windows Search index through OleDb. JSON is a reasonable choice for small preferences because it is easy to inspect. Tags, however, represent a many-to-many relationship: one file can have multiple tags, and one tag can apply to many files. That relationship becomes more natural to manage in a relational database.")]
    story += [p("Existing functionality", "H2Clearspace")]
    story += [p("Clearspace stores user-created tags and tag assignments in a JSON file. Each tag has an ID, name, and color. The assignment structure maps each file path to a list of tag IDs. The application also stores settings in JSON, saves its file index in a custom binary file, and can query the existing Windows Search index through OleDb.")]
    story += [p("Show on screen", "H2Clearspace"), bullet("Clearspace/Services/TagService.cs"), bullet("Clearspace/Services/SettingsService.cs"), bullet("Clearspace/Services/FileIndexStore.cs"), bullet("Clearspace/Services/WindowsSearchService.cs")]
    story += [p("Analysis findings", "H2Clearspace")]
    story += [bullet("Every tag update rewrites the complete JSON document. This becomes less efficient as the tag catalog and assignments grow."),
              bullet("Tag filtering and cleanup can scan all assignments instead of relying on indexed database queries."),
              bullet("Referential integrity is maintained manually. Deleting a tag loops through every path to remove orphaned IDs."),
              bullet("JSON storage provides no transactional multi-step updates, foreign keys, or migration mechanism. Paths are record identities, so moves outside Clearspace may leave stale metadata.")]
    story += [p("Planned enhancement", "H2Clearspace")]
    story += [p("The current JSON approach has limits. Every tag update rewrites the complete document. Tag cleanup scans assignments instead of relying on foreign keys, and multi-step changes have no database transaction. My planned database enhancement will migrate tags to local SQLite storage. I will create normalized Tags and FileTags tables, with an optional FolderProfiles table. I will enable foreign keys, index FileTags by tag ID, enforce case-insensitive unique tag names, use parameterized SQL, and wrap multi-step writes in transactions. I will also create a one-time migration from the JSON data, validate the migrated records, and retain the JSON file as a backup until migration succeeds. This demonstrates normalized relational design, keys, indexes, transactions, migration planning, and secure parameterized access.")]
    story += [p("Outcome connection", "H2Clearspace"), p("This demonstrates relational design, normalization, primary and foreign keys, indexing, transactions, migration planning, and secure parameterized database access.")]

    story += [p("Closing", "H1Clearspace")]
    story += [p("Clearspace already has a strong foundation: a layered Windows desktop architecture, a performance-conscious in-memory index, and persistent organization features. The proposed enhancements are deliberately connected.")]
    story += [bullet("Refactoring services and adding tests makes the application safer to evolve."),
              bullet("Improving the index makes folder data more accurate and useful."),
              bullet("Migrating tags to SQLite makes metadata scalable, reliable, and easier to query.")]
    story += [p("Together, these enhancements demonstrate the ability to evaluate tradeoffs, implement professional-quality software, use appropriate data structures and algorithms, manage persistent data, and consider reliability and security throughout the design.")]
    story += [PageBreak(), p("On-screen walkthrough: exact talking points", "H1Clearspace")]
    story += [p("Use these paragraphs when the corresponding file is visible. You do not need to read every word exactly, but each one is written as a natural explanation of what the viewer should see.")]

    story += [p("1. Open Clearspace/ARCHITECTURE.md", "H2Clearspace")]
    story += [p("I am starting with the architecture document because it gives the overall design of Clearspace. The application is organized into Native code for Windows calls, Models for application data, Services for reusable behavior, Commands for user actions, and ViewModels for user-interface state. This organization is a strength because it separates most Windows-specific details from the user interface. It also gives the project a clear structure for future changes. As I review the rest of the code, I will refer back to this separation and evaluate whether the actual implementation continues to follow it consistently.")]

    story += [p("2. Open Clearspace/ViewModels/MainViewModel.cs", "H2Clearspace")]
    story += [p("This file is the main coordinator for the application. It connects the interface to navigation, searching, saved layouts, sidebar entries, tags, and status information. I want to point out that this class is more than 2,000 lines long. It does important work, but it now owns several unrelated responsibilities. For example, I can show the RunTreeSearchAsync method near line 1085, where it coordinates local results, the Clearspace index, Windows Search, and a fallback directory crawl. My enhancement will split responsibilities like search coordination, navigation, and sidebar management into smaller services that are easier to test and maintain.")]

    story += [p("3. Open Clearspace/Commands and Clearspace/Services/FileOperationService.cs", "H2Clearspace")]
    story += [p("The Commands folder shows an intentional design decision. User actions are represented separately from the interface, so menu items and keyboard actions do not need to contain all of their behavior directly. FileOperationService is where the command eventually performs Windows shell operations such as copy, move, rename, and delete. The limitation is that these methods return only a Boolean result. A false result does not tell the user whether the action was cancelled, denied because of permissions, or failed for another reason. I plan to return a richer result object so the interface can provide clear feedback without exposing unnecessary technical details.")]

    story += [p("4. Open Clearspace/Services/FileIndex.cs", "H2Clearspace")]
    story += [p("This file is the core data-structure example in Clearspace. IndexEntry stores file information such as size, timestamps, attributes, a name offset, and a parent index. The name itself is stored in a shared character array rather than in a separate string object for every entry. That design helps control memory use when a drive contains a large number of files. The ParentIndex field lets Clearspace reconstruct a full path only when it needs to show a result. I also want to point out the folded name pool, which lowercases names once so a search does not repeatedly convert every filename while the user is typing.")]

    story += [p("5. Open Clearspace/Services/FileIndexBuilder.cs", "H2Clearspace")]
    story += [p("This builder creates the in-memory index by walking directories in the background. It uses a stack to keep track of directories that still need to be scanned, and it avoids following reparse points so that symbolic links and junctions do not create cycles. The issue I identified is visible near line 11: MaxDepth is set to 32. A file deeper than that limit may never enter the index. This is important because the product goal is to search comprehensively. My enhancement will remove the arbitrary cap or make it configurable, then add tests using deeply nested folder structures to prove that the index returns complete results.")]

    story += [p("6. Open Clearspace/Services/FileSearchService.cs", "H2Clearspace")]
    story += [p("This is the fallback search path used when the in-memory index cannot answer a search. It uses concurrent workers to process directories and publishes results in batches so the interface stays responsive. That is a good user-experience choice. However, the same completeness concern appears here: the fallback MaxDepth is set to 24, which is even lower than the index builder's limit. I will explain that the two search paths should agree on correctness. In the enhancement, I will test both paths with the same deep directory tree, cancellation scenario, and rename or deletion cases so that a fallback does not silently return fewer results.")]

    story += [p("7. Open Clearspace/Services/TagService.cs", "H2Clearspace")]
    story += [p("This file is the strongest starting point for the database enhancement. TagData stores a list of tag definitions and a dictionary that maps each file path to a list of tag IDs. This works for a small set of user data, but each save rewrites the full JSON document. Tag cleanup is also manual. When a tag is deleted, the program must scan every path and remove the tag ID itself. That means the application, rather than the data store, is responsible for keeping relationships valid. I will replace this JSON structure with SQLite tables for Tags and FileTags, which directly represent the many-to-many relationship.")]

    story += [p("8. Open Clearspace/Services/FileIndexStore.cs, then WindowsSearchService.cs", "H2Clearspace")]
    story += [p("I am ending with these two services to show that Clearspace already uses different storage approaches for different needs. FileIndexStore saves the performance-oriented file index in a compact binary format with a magic value and format version. WindowsSearchService uses OleDb to query the Windows-maintained search index when available. My database enhancement does not replace those systems. Instead, it improves the smaller user-managed metadata system: tags. SQLite will give tags transactional writes, foreign-key cleanup, indexed queries, and a safe migration path from JSON, while the file index and Windows Search continue serving their current performance roles.")]
    doc.build(story)


if __name__ == "__main__":
    build()
