from pathlib import Path
import re
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import inch
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak, Table, TableStyle, KeepTogether


ROOT = Path(r"E:\GithubRepos\File Explorer Remake")
OUT = ROOT / "output" / "pdf" / "Clearspace_CS499_Detailed_Speaking_Guide.pdf"
OUT.parent.mkdir(parents=True, exist_ok=True)

NAVY = colors.HexColor("#244B6E")
BLUE = colors.HexColor("#2E6DA4")
PALE = colors.HexColor("#EAF2F8")
LIGHT = colors.HexColor("#F8FAFC")
TEXT = colors.HexColor("#203040")

# Embed standard Windows fonts so the rendered PDF remains consistent on every page.
pdfmetrics.registerFont(TTFont("GuideArial", r"C:\Windows\Fonts\arial.ttf"))
pdfmetrics.registerFont(TTFont("GuideArialBold", r"C:\Windows\Fonts\arialbd.ttf"))

styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name="CoverTitle", parent=styles["Title"], fontName="GuideArialBold", fontSize=25, leading=31, textColor=NAVY, alignment=TA_CENTER, spaceAfter=14))
styles.add(ParagraphStyle(name="CoverSub", parent=styles["BodyText"], fontName="GuideArial", fontSize=12, leading=17, textColor=TEXT, alignment=TA_CENTER))
styles.add(ParagraphStyle(name="H1x", parent=styles["Heading1"], fontName="GuideArialBold", fontSize=14.5, leading=18, textColor=BLUE, spaceAfter=6))
styles.add(ParagraphStyle(name="H2x", parent=styles["Heading2"], fontName="GuideArialBold", fontSize=13, leading=16, textColor=NAVY, spaceBefore=8, spaceAfter=5))
styles.add(ParagraphStyle(name="Bodyx", parent=styles["BodyText"], fontName="GuideArial", fontSize=8.7, leading=11.8, textColor=TEXT, spaceAfter=4))
styles.add(ParagraphStyle(name="Say", parent=styles["BodyText"], fontName="GuideArial", fontSize=8.7, leading=11.8, textColor=TEXT, spaceAfter=0))
styles.add(ParagraphStyle(name="Small", parent=styles["BodyText"], fontName="GuideArial", fontSize=8.1, leading=10.5, textColor=TEXT, spaceAfter=3))
styles.add(ParagraphStyle(name="Label", parent=styles["BodyText"], fontName="GuideArialBold", fontSize=7.7, leading=9, textColor=NAVY, alignment=TA_CENTER))


def P(text, style="Bodyx"):
    return Paragraph(text, styles[style])


def footer(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(colors.HexColor("#D7E1EA"))
    canvas.line(0.65 * inch, 0.52 * inch, 7.85 * inch, 0.52 * inch)
    canvas.setFont("GuideArial", 8.5)
    canvas.setFillColor(colors.HexColor("#63788C"))
    canvas.drawString(0.65 * inch, 0.31 * inch, "Clearspace CS 499 - Detailed Speaking Guide")
    canvas.drawRightString(7.85 * inch, 0.31 * inch, f"Page {doc.page}")
    canvas.restoreState()


def card(label, text, tint=LIGHT):
    # Keep the cue embedded with its explanation. This is more reliable in a
    # long PDF than a narrow, independently rendered label column.
    content = P(f"<font color='#244B6E'><b>{label}:</b></font> {text}", "Say")
    t = Table([[content]], colWidths=[7.2 * inch])
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), tint),
        ("BOX", (0, 0), (-1, -1), 0.45, colors.HexColor("#C8D6E2")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 8),
        ("RIGHTPADDING", (0, 0), (-1, -1), 8),
        ("TOPPADDING", (0, 0), (-1, -1), 5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
    ]))
    return t


def guide(story, title, show, say, explain, watch, transition, *more):
    # One section deliberately has an extra spoken detail. Keep that detail in
    # the document without making the reusable card layout harder to read.
    if more:
        say = say + "<br/><br/>" + explain
        explain, watch, transition = watch, transition, more[0]
    title = re.sub(r"(\d+:\d+)-(\d+:\d+)", r"\1 to \2", title)
    story.append(P(title, "H1x"))
    story.append(card("SHOW", show))
    story.append(Spacer(1, 6))
    story.append(card("SAY THIS", say, colors.white))
    story.append(Spacer(1, 6))
    story.append(card("HOW IT WORKS", explain))
    story.append(Spacer(1, 6))
    story.append(card("POINT OUT", watch, colors.white))
    story.append(Spacer(1, 6))
    story.append(card("TRANSITION", transition))


story = []
story += [Spacer(1, 1.45 * inch), P("Clearspace CS 499", "CoverTitle"), P("Detailed Speaking Guide", "CoverTitle"), Spacer(1, 0.15 * inch), P("A companion to your 30-minute code-review runbook. Read the SAY THIS blocks naturally; they are written as full explanations, not just reminders.", "CoverSub"), Spacer(1, 0.35 * inch)]
intro = Table([[P("How to use this guide", "H2x")], [P("Keep the original runbook beside you for timing and screen order. Use this PDF when you need more detail about what the code is doing. You do not need to memorize every word. Keep the technical words that help your explanation, but say them in your own voice. When a section says planned enhancement, say planned or will implement - do not imply that the new code already exists.", "Bodyx")]], colWidths=[6.7 * inch])
intro.setStyle(TableStyle([("BACKGROUND", (0,0),(-1,0), PALE), ("BACKGROUND", (0,1),(-1,-1), LIGHT), ("BOX", (0,0),(-1,-1),0.5,colors.HexColor("#C8D6E2")), ("LEFTPADDING",(0,0),(-1,-1),14), ("RIGHTPADDING",(0,0),(-1,-1),14), ("TOPPADDING",(0,0),(-1,-1),10), ("BOTTOMPADDING",(0,0),(-1,-1),10)]))
story += [intro, Spacer(1, 0.25 * inch), P("Three rules that keep the video strong", "H2x"), P("1. Describe evidence that is visibly on screen. 2. Explain the reason behind the design, not just the class name. 3. Pause after a key claim so the viewer can see the code you are discussing.", "Bodyx"), PageBreak()]

guide(story, "0:00-1:30 - Opening the running application",
      "Have Clearspace already open to a safe demo folder. The sidebar, file list, and search field should be visible.",
      "This is Clearspace, a Windows file-management application. I will first show the user experience so the code review has a real context. Then I will review the current implementation in three categories: software engineering and design, algorithms and data structures, and database design. For each category, I will identify a strength, explain a limitation I found in the current code, and describe the enhancement I plan to make.",
      "This opening establishes the scope. The application is not just a set of classes; it helps users browse folders, search for files, perform file operations, and assign tags. Those user actions are what the ViewModels, Commands, and Services cooperate to support.",
      "Point to the current folder in the sidebar, then the item list. Move your cursor slowly. Do not begin discussing code yet; let the viewer understand the product first.",
      "Before I open the code, I want to quickly demonstrate the actions that the code is responsible for supporting.")
story.append(PageBreak())

guide(story, "1:30-3:30 - Browsing, selecting, and navigating",
      "Click one ordinary folder, select a file, and navigate once using the sidebar or a folder item. Avoid personal or sensitive files.",
      "A typical user begins by navigating into a folder and selecting an item. The program must keep track of the selected location, the selected item, what should be shown in the center panel, and what actions are currently allowed. When I open the code later, the MainViewModel is the main coordinator for much of that screen state. The point is not that one class does every job. The point is that it connects user-interface state to focused services that perform the actual file-system work.",
      "In a WPF application, the view is the window the user sees. A ViewModel supplies the values and commands that the view binds to. The ViewModel is an intermediary: it should coordinate a user action without putting Windows shell details or raw file traversal directly inside the window layout.",
      "While an item is selected, mention how the selection affects possible actions such as opening, copying, moving, renaming, deleting, tagging, or showing properties. This makes your later command discussion concrete.",
      "Navigation establishes the basic workflow. The next feature, search, is useful because it lets users find items without manually opening every folder.")
story.append(PageBreak())

guide(story, "3:30-5:00 - Search and tags from the user view",
      "Type a simple search. If possible, select a tagged item or open the tag menu after the search results appear.",
      "Search is a good example of a feature that looks simple to the user but has several technical paths underneath it. Clearspace can use visible-folder results, its own in-memory index, Windows Search, and a background directory crawl when necessary. That layered approach is important because quick results and complete results are not always the same thing. Tags are different from folders: they let a user group related files without moving those files on disk. That creates a many-to-many relationship, because one file can have several tags and one tag can apply to many files.",
      "You are previewing two later categories. The search path leads to the algorithms and data-structures review. The tag relationship leads to the database review. Do not claim a certain search source was used for the specific query unless the application visibly says so; instead explain the sources the code is designed to consult.",
      "Let results settle. If a tag menu is not available, say: 'The important point is that tag metadata is stored separately from the file itself.' That is enough evidence for the later persistence discussion.",
      "Now that the app behavior is clear, I will start the software engineering and design review by looking at how the project is organized.")
story.append(PageBreak())

guide(story, "5:00-7:00 - Project structure",
      "Open Clearspace/ARCHITECTURE.md and show the folder layout. Pause on Native, Models, Services, Commands, and ViewModels.",
      "The project is organized into layers with different responsibilities. Native code isolates Windows-specific calls. Models represent application data. Services contain reusable behavior, such as file operations, search, indexing, and tag persistence. Commands represent user actions, which helps the interface trigger behavior without embedding all the behavior in a button click. ViewModels hold interface-facing state and coordinate the work. This is a strong starting design because it gives the project natural places for different kinds of code to live.",
      "Say why separation matters: 'When a responsibility has one clear home, a change has a smaller blast radius. It is easier to test a service than a large screen controller, and easier to change Windows-specific details when they are not scattered through the user interface.'",
      "Do not say that every responsibility is perfectly separated. Your review becomes stronger when you immediately follow this strength with the place where the main coordinator has grown too large.",
      "This is the intended structure. Next I will show the central ViewModel where several of these responsibilities currently meet.")
story.append(PageBreak())

guide(story, "7:00-10:00 - MainViewModel and search coordination",
      "Open Clearspace/ViewModels/MainViewModel.cs near RunTreeSearchAsync, around line 1085. Keep calls to the index, Windows Search, and FileSearchService visible.",
      "This is the MainViewModel, the main coordinator for the screen. It is responsible for navigation, loading folders, search, sidebar entries, saved layout preferences, tags, and status information. That central role is understandable, but it has made the class broad. RunTreeSearchAsync is a useful example. One user search can be coordinated across local results, the Clearspace in-memory index, Windows Search, and a fallback directory crawl. The method is performing orchestration: it decides which collaborators to call and combines results for the user interface.",
      "Continue with: 'The concern is not that coordination exists. The concern is that one class now has many independent reasons to change. A change to navigation, tag state, search behavior, or sidebar state can all affect this same ViewModel. That makes testing and maintenance harder because a test may need to construct more dependencies than the feature actually needs.'",
      "Point at the method signature and each service call. Spend a few seconds on the names so the viewer can tie your words to the evidence. If the method has asynchronous calls, say: 'The async pattern keeps lengthy work from blocking the interface while results are collected.'",
      "The ViewModel decides what work should happen. The next files show how user actions are represented and how file-system work is carried out.")
story.append(PageBreak())

guide(story, "10:00-12:30 - Commands and FileOperationService",
      "Open the Clearspace/Commands folder, then Clearspace/Services/FileOperationService.cs. Locate copy, move, rename, delete, and their Boolean results.",
      "The Commands folder is a good design decision because it keeps user actions separate from visual controls. A menu item or keyboard shortcut can invoke a command, and the command can call the right service. FileOperationService then performs Windows shell operations such as copy, move, rename, and delete. This keeps the detailed file-system behavior out of the view itself, which supports a cleaner separation between user interface and application logic.",
      "Then explain the limitation: 'The current service returns a Boolean value for several operations. A Boolean tells the caller only success or failure. It does not communicate whether the user canceled the operation, a permission issue occurred, a source file no longer existed, or some other Windows error happened. Those are different user experiences, but a single false value treats them the same.'",
      "A simple definition to say aloud: 'A result object is a small structured record that can carry success status, an error category, a message, and possibly the affected path. It is more informative than a true-or-false answer.'",
      "Point at the operation methods and the Boolean return type. Pause after 'different user experiences' because that is the reason for your proposed change.",
      "I have shown the current strengths and the specific limitation. I will now state the software engineering enhancement in a clear, implementable way.")
story.append(PageBreak())

guide(story, "12:30-15:00 - Software engineering enhancement and close",
      "Leave MainViewModel.cs or FileOperationService.cs visible. You may briefly scroll between them while speaking.",
      "My software engineering enhancement will strengthen the existing architecture rather than replace it. I will split the oversized MainViewModel into focused responsibilities, such as navigation coordination, search coordination, and sidebar state. I will use focused interfaces so each part depends only on the services it actually needs. I will also replace simple Boolean file-operation outcomes with a richer result object that clearly distinguishes success, cancellation, permission issues, and other failures. Finally, I will add automated tests for search parsing, tag behavior, indexing edge cases, and user-visible operation failures.",
      "Explain the benefit in plain language: 'This change makes it easier to understand one piece of behavior at a time. It makes failures easier to explain to a user, and it gives tests a smaller, more controlled target. The goal is maintainability and reliable behavior, especially because this application works with user files.'",
      "Use the word planned several times. You are reviewing existing code and describing an enhancement. Do not say 'I already split' or 'this database now uses' unless the new implementation truly exists.",
      "Clearly say: 'That concludes my software engineering and design review.' Then pause for about two seconds.",
      "I will now move to algorithms and data structures, starting with the search experience a user sees.")
story.append(PageBreak())

guide(story, "15:00-16:30 - Why search needs data structures",
      "Return to the running app and perform a simple search once more. Then switch back to the code editor.",
      "A user expects search to feel immediate even when a drive contains a large number of files. Scanning every directory from the disk on every keystroke would be expensive and could make the application feel slow. Clearspace therefore has several layers: quick visible-folder results, an in-memory index, Windows Search, and a fallback crawl. The data structure I will show next is the in-memory index. It keeps information that has already been collected in a compact form so repeated searches can inspect memory instead of repeatedly walking the disk.",
      "A useful phrase is: 'An index trades some extra memory and maintenance work for faster lookup later.' This is the central algorithmic tradeoff. Indexing is not free: the app must build it, update it, and decide what to do when a drive or watcher has a problem.",
      "Do not overpromise that every search is instant. Say that the design tries to return useful results quickly and uses a fallback when index coverage is incomplete.",
      "The next file shows the exact in-memory representation that makes that repeated search possible.")
story.append(PageBreak())

guide(story, "16:30-19:30 - FileIndex: compact entries and search",
      "Open Clearspace/Services/FileIndex.cs. Show IndexEntry, NameOffset, ParentIndex, the character pools, folded names, and Search.",
      "FileIndex contains the in-memory data structure used for indexed search. Each IndexEntry stores information such as file size, timestamps, attributes, a name offset, and a parent index. Instead of storing a separate full string object inside every entry, file names are stored in shared character pools. The name offset tells the program where the name begins in that pool. This is a memory-conscious design for a large collection of files, because repeated per-object overhead can add up when many entries are indexed.",
      "Continue: 'ParentIndex represents the relationship between an item and its containing folder. When a result needs a full path, the application can reconstruct it by following parent relationships, rather than copying the full path into every entry. The folded-name pool stores a normalized lowercase form once, so each search does not have to repeatedly convert the same file names to a comparable form.'",
      "Explain the search: 'The Search method scans candidate entries, applies the user's filters, and uses parallel chunks of work where appropriate. Parallelism can improve throughput on a large collection, but the result still needs safe coordination and predictable ordering for the user interface.'",
      "Point at each field as you name it. If you see Parallel.For, say it is splitting independent chunks among workers. Do not claim a specific complexity unless you can state its assumptions; focus on the visible tradeoff: compact storage and faster repeated matching.",
      "The index structure is only valuable if it is built safely and completely. Next I will show the background traversal that creates it.")
story.append(PageBreak())

guide(story, "19:30-22:00 - FileIndexBuilder: traversal, stack, and depth cap",
      "Open Clearspace/Services/FileIndexBuilder.cs. Show MaxDepth = 32, the pending stack, and the reparse-point check.",
      "FileIndexBuilder creates the index by walking directories in the background. It uses a stack of directories that still need to be scanned. A stack supports a depth-first style of traversal: the builder removes one pending directory, records its contents, and adds its child directories when they are safe to visit. The builder also checks for reparse points. That is important on Windows because symbolic links and junctions can create loops or take the traversal outside the intended directory tree. Avoiding those points is a defensive choice that protects the indexing process from cycles.",
      "Then make the review point: 'Near the top of this file, MaxDepth is set to 32. That means a directory beyond that depth can be excluded from the in-memory index. The code may have adopted this cap as a safety or performance guard, but it creates a correctness question. If the app presents search as comprehensive, users may not know that deeply nested files could be absent from indexed results.'",
      "Define depth simply: 'Depth is how many folder levels an item is below the starting location. A file directly inside the root has a shallow depth; a file nested through many folders has a deeper depth.'",
      "Point at MaxDepth, then the stack and the check that increments or compares depth. Pause on the exact cap so your criticism is anchored to evidence.",
      "The fallback search has its own traversal logic. I will compare it next because it reveals a second, different depth limit.")
story.append(PageBreak())

guide(story, "22:00-24:00 - FileSearchService fallback search",
      "Open Clearspace/Services/FileSearchService.cs. Show MaxDepth = 24, worker tasks, queue, and batched results.",
      "FileSearchService is the fallback path for locations that are not completely covered by the in-memory index. It uses concurrent workers so directory scanning can continue in the background, and it publishes results in batches so the user interface can update without waiting for every directory to finish. That is a responsive design choice: the user can begin seeing matches while more scanning continues.",
      "The concern is that this fallback has another hard-coded traversal cap. Here MaxDepth is 24, which is different from the index builder's limit of 32. This means the fallback can be less complete than the main index. A user could receive different coverage depending on which path handles the search, even though both features are described as search. That inconsistency is a correctness and user-trust concern, not only a performance concern.",
      "Explain cancellation: 'A background search also needs cancellation because the user may type a new query or navigate away. The code should stop work that is no longer useful, while still keeping any shared result collection in a valid state.'",
      "Slowly compare the 24 value with the earlier 32. Mention the queue, worker tasks, and batch publishing only if visible. If a detail is not on screen, leave it out rather than guessing.",
      "With both search paths reviewed, I can now state the algorithms and data-structures enhancement and explain how I will evaluate it.")
story.append(PageBreak())

guide(story, "24:00-26:00 - Algorithms enhancement and close",
      "Return to FileIndex.cs and keep ParentIndex visible. You can briefly revisit the two MaxDepth constants if helpful.",
      "My algorithms and data-structures enhancement has two connected parts. First, I will make traversal depth configurable or remove the arbitrary caps after validating safe cycle protection and cancellation. I will test both search paths against the same deep directory structures so completeness does not depend on which source handled the search. Second, I will add folder-size aggregation to the existing index. Because each entry already stores its size and parent index, I can perform a bottom-up pass after indexing: each file contributes its size to its parent, and each folder's accumulated size contributes to its parent. That gives the application a folder total without walking the disk again every time the user asks for it.",
      "Explain bottom-up: 'Bottom-up means I calculate the deepest items first, then roll their totals upward through the parent relationships. A parent folder gets the total of its own files plus the totals of its child folders.'",
      "Finish the tradeoff: 'I will measure memory and build time, add correctness tests for nested folders and changed files, and keep index health per volume rather than treating one watcher failure as a failure for every drive. The goal is a design that is both fast and trustworthy.'",
      "Clearly say: 'That concludes my algorithms and data-structures review.' Pause, then return to the running app.",
      "I will now move to the database category by returning to the tag feature the user sees.")
story.append(PageBreak())

guide(story, "26:00-27:30 - Tags as a many-to-many relationship",
      "Return to Clearspace. Select an item and show a tag, tag menu, or tag-related search filter.",
      "Tags are user-managed metadata. Unlike a folder location, a tag can be attached to multiple files, and a file can have multiple tags. For example, a file could be tagged both School and Portfolio, while the School tag can be attached to many different files. That is a many-to-many relationship. The user experience is simple, but behind the screen the application must preserve the relationship accurately as tags are added, removed, renamed, or deleted.",
      "Tie it to persistence: 'The question for this category is not whether tags work for a small amount of data. The question is whether the current storage keeps relationships reliable and scalable as the user assigns more tags over time.'",
      "If you cannot show a tag being added live, say: 'I am showing the tag area rather than changing real files during the recording. The next code file shows the storage model that supports these tag relationships.'",
      "The user-facing feature is straightforward. Next I will show the service that stores tag definitions and file assignments today.")
story.append(PageBreak())

guide(story, "27:30-29:00 - TagService and JSON persistence",
      "Open Clearspace/Services/TagService.cs. Show TagData near line 43, then Load, Save, and Delete.",
      "TagService shows the current persistence design. TagData contains a list of tag definitions and a dictionary that maps a file path to a list of tag IDs. The definitions identify the available tags. The assignment dictionary records which tag identifiers belong to each file path. This correctly represents the basic many-to-many idea in application memory, and JSON is simple to inspect and convenient for a small amount of data.",
      "Then explain the limitation carefully: 'With this design, an update rewrites the full JSON document. When a tag is deleted, application code manually scans assignments to remove tag IDs that would otherwise be orphaned. That puts relationship cleanup in the application code. It also means a multi-step change does not have database transaction protection, where a group of related changes either all succeed together or all roll back together.'",
      "Say this definition if needed: 'Data integrity means the stored data stays internally consistent. For tags, that means an assignment should not refer to a tag that no longer exists.'",
      "Point first to Definitions and Assignments. Then scroll to Save and Delete. Say 'manual cleanup loop' while the code is visible, but do not say JSON is always bad; it is a reasonable simple format with limits as data and relationships grow.",
      "I have identified the current storage model and its limits. I will close with the local SQLite design that directly addresses those limits.")
story.append(PageBreak())

guide(story, "29:00-30:00 - SQLite enhancement and final close",
      "Leave TagService.cs open. If you finish early, briefly show FileIndexStore.cs or WindowsSearchService.cs, but do not start a new deep explanation.",
      "My planned database enhancement is a local SQLite database for tag metadata. I will use a Tags table for tag definitions and a FileTags table for assignments. FileTags will hold the connection between a file path and a tag ID, which is the relational form of the many-to-many relationship. I will enable foreign keys so an assignment cannot refer to a missing tag. I will add an index for tag lookups, use parameterized queries, and perform related updates in transactions. Before changing storage, I will migrate existing JSON data safely and keep a backup until the migration succeeds.",
      "Close the whole review with: 'Clearspace already has a strong foundation: it separates user-interface state, commands, and services; it uses an in-memory index to support repeated search; and it provides useful tag metadata. My planned enhancements build on that foundation. They improve maintainability by narrowing responsibilities, improve search correctness through safer traversal and folder aggregation, and improve data reliability by storing tag relationships in SQLite. Thank you for reviewing my code-review plan.'",
      "If you are short on time, skip optional files and read the close slowly. If you are early, say: 'The SQLite work improves tag persistence only; it does not replace the high-performance file index or Windows Search.'",
      "End at or very close to 30 minutes. Stop after your final thank-you; do not add a new feature or unplanned claim.",
      "This concludes my code review. The proposed work builds on the existing application rather than replacing it.")
story.append(PageBreak())

story.append(P("Quick recovery lines", "H1x"))
story.append(P("Use these only if you lose your place. They help you keep the video linear without sounding like you are restarting.", "Bodyx"))
for label, txt in [
    ("Need to slow down", "Let me pause on this file for a moment, because this line is the evidence for the design decision I am describing."),
    ("Need to move on", "The key point is the tradeoff: this approach improves one part of the experience, but it creates the limitation I have identified. I will now show the next part of the implementation."),
    ("Do not know a detail", "I do not want to overstate what this line guarantees. What the code clearly shows is [name the visible method, field, or constant], and that is the basis for my planned improvement."),
    ("Tag UI unavailable", "The interface is not showing the tag action in this exact state, so I will use the persistence code to explain how tag definitions and file assignments are stored."),
    ("Search takes time", "While the results load, this is a useful reminder that search may combine quick indexed sources with a fallback scan when coverage is incomplete."),
    ("Closing early", "To summarize, the three enhancements are focused responsibilities and richer operation results, safer and more complete indexing, and relational tag storage with SQLite.")
]:
    story.append(Spacer(1, 5)); story.append(card(label, txt, colors.white))

doc = SimpleDocTemplate(str(OUT), pagesize=letter, rightMargin=0.65*inch, leftMargin=0.65*inch, topMargin=0.62*inch, bottomMargin=0.72*inch, title="Clearspace CS 499 Detailed Speaking Guide", author="Codex")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print(OUT)
