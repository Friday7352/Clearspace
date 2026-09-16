from pathlib import Path
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import inch
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak, Table, TableStyle

ROOT = Path(r"E:\GithubRepos\File Explorer Remake")
OUT = ROOT / "output" / "pdf" / "Clearspace_CS499_Detailed_Speaking_Guide.pdf"
OUT.parent.mkdir(parents=True, exist_ok=True)

pdfmetrics.registerFont(TTFont("ScriptArial", r"C:\Windows\Fonts\arial.ttf"))
pdfmetrics.registerFont(TTFont("ScriptArialBold", r"C:\Windows\Fonts\arialbd.ttf"))

NAVY = colors.HexColor("#244B6E")
BLUE = colors.HexColor("#2E6DA4")
PALE = colors.HexColor("#EAF2F8")
VERY_LIGHT = colors.HexColor("#F7FAFC")
TEXT = colors.HexColor("#203040")

styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name="TitleX", fontName="ScriptArialBold", fontSize=25, leading=31, textColor=NAVY, alignment=TA_CENTER, spaceAfter=12))
styles.add(ParagraphStyle(name="SubtitleX", fontName="ScriptArial", fontSize=12, leading=17, textColor=TEXT, alignment=TA_CENTER))
styles.add(ParagraphStyle(name="SectionX", fontName="ScriptArialBold", fontSize=16, leading=20, textColor=BLUE, spaceAfter=8))
styles.add(ParagraphStyle(name="LabelX", fontName="ScriptArialBold", fontSize=9.5, leading=12, textColor=NAVY))
styles.add(ParagraphStyle(name="BodyX", fontName="ScriptArial", fontSize=10.6, leading=15.4, textColor=TEXT, spaceAfter=9))
styles.add(ParagraphStyle(name="SmallX", fontName="ScriptArial", fontSize=9, leading=12.5, textColor=TEXT))

def P(text, style="BodyX"):
    return Paragraph(text, styles[style])

def footer(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(colors.HexColor("#D5E0EA"))
    canvas.line(0.65*inch, 0.53*inch, 7.85*inch, 0.53*inch)
    canvas.setFillColor(colors.HexColor("#63788C"))
    canvas.setFont("ScriptArial", 8.5)
    canvas.drawString(0.65*inch, 0.31*inch, "Clearspace CS 499 - Word-for-word recording script")
    canvas.drawRightString(7.85*inch, 0.31*inch, f"Page {doc.page}")
    canvas.restoreState()

def cue(text):
    content = P(f"<b>ON SCREEN (do not read this out loud)</b><br/>{text}", "SmallX")
    t = Table([[content]], colWidths=[7.2*inch])
    t.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,-1), VERY_LIGHT),
        ("BOX", (0,0), (-1,-1), 0.45, colors.HexColor("#C8D6E2")),
        ("LEFTPADDING", (0,0), (-1,-1), 10), ("RIGHTPADDING", (0,0), (-1,-1), 10),
        ("TOPPADDING", (0,0), (-1,-1), 7), ("BOTTOMPADDING", (0,0), (-1,-1), 7),
    ]))
    return t

def say(text):
    content = P(f"<b>SAY THIS WORD FOR WORD</b><br/><br/>{text}", "BodyX")
    t = Table([[content]], colWidths=[7.2*inch])
    t.setStyle(TableStyle([
        ("BACKGROUND", (0,0), (-1,-1), colors.white),
        ("BOX", (0,0), (-1,-1), 0.55, colors.HexColor("#B9CCDD")),
        ("LEFTPADDING", (0,0), (-1,-1), 12), ("RIGHTPADDING", (0,0), (-1,-1), 12),
        ("TOPPADDING", (0,0), (-1,-1), 8), ("BOTTOMPADDING", (0,0), (-1,-1), 8),
    ]))
    return t

def section(story, title, screen, speech):
    story.append(P(title, "SectionX"))
    story.append(cue(screen))
    story.append(Spacer(1, 10))
    story.append(say(speech))
    story.append(PageBreak())

def plain_section(story, title, screen, speech):
    # A deliberately simple page for a long spoken section.
    story.append(P(title, "SectionX"))
    story.append(P(f"<b>ON SCREEN (do not read this out loud):</b> {screen}", "SmallX"))
    story.append(Spacer(1, 14))
    story.append(P(f"<b>SAY THIS WORD FOR WORD</b><br/><br/>{speech}", "BodyX"))
    story.append(PageBreak())

story = [Spacer(1, 1.25*inch), P("Clearspace CS 499", "TitleX"), P("Word-for-word Recording Script", "TitleX"), Spacer(1, 0.15*inch), P("This replaces the detailed speaking guide. Read only the large SAY THIS box. The small ON SCREEN box tells you what to open or point at, but you do not say it aloud.", "SubtitleX"), Spacer(1, 0.45*inch)]
story.append(cue("Keep Clearspace, your code editor, and this script open. Speak slowly, point at the code while you describe it, and pause for two or three seconds when a new file appears."))
story.append(PageBreak())

section(story, "0:00 to 1:30 - Start the video", "Clearspace open on a safe normal folder. Keep the sidebar, file list, and search box visible.",
"Hello. This is my Clearspace code review. Clearspace is a Windows file-management application that lets a user browse folders, search for files, perform file operations, and organize files with tags. In this video, I will review the current code in three categories: software engineering and design, algorithms and data structures, and database design. For each category, I will explain what the current code does, identify one area that can be improved, and explain the enhancement I plan to make. I will begin by showing the application from the user point of view, and then I will connect each feature to the code that supports it.")

section(story, "1:30 to 3:30 - Show normal file browsing", "Click one folder, select one ordinary file, and navigate once.",
"A user begins by choosing a folder and selecting a file. The application has to remember the current folder, the selected item, the items that should appear in the main panel, and the actions the user is allowed to take. For example, selecting a file can enable actions such as open, copy, move, rename, delete, view properties, or assign tags. In a WPF application, the window is called the view. The ViewModel supplies the information and commands that the view needs. In Clearspace, the ViewModel coordinates the user-interface state while services perform the detailed file-system work. This separation is important because the window should not contain all of the Windows shell code or all of the search logic itself.")

section(story, "3:30 to 5:00 - Show search and tags", "Run a simple search. If possible, show one tag or the tag menu.",
"Search and tags are two important features in Clearspace. Search helps the user locate files without opening folders one by one. Tags let a user group related files without changing where those files are stored on disk. One file can have more than one tag, and the same tag can belong to many files. Later in the video, search will lead into my algorithms and data-structures review, and tags will lead into my database review. Now that I have shown the main user experience, I will begin the software engineering and design category by opening the project structure.")

section(story, "5:00 to 7:00 - Software engineering: project structure", "Open Clearspace/ARCHITECTURE.md. Point to Native, Models, Services, Commands, and ViewModels.",
"The project is organized into several areas with different responsibilities. Native code contains Windows-specific calls. Models represent application data. Services contain reusable behavior, such as file operations, search, indexing, and tag storage. Commands represent actions the user can take, such as copying or deleting a file. ViewModels hold information needed by the user interface and coordinate the work between the interface and the services. This is a good starting design because it gives each type of code a clear home. It also makes the program easier to maintain because a change to Windows-specific behavior does not have to be made directly in the user interface. Next, I will show the MainViewModel, which is the central controller for much of the interface.")

section(story, "7:00 to 10:00 - Software engineering: MainViewModel", "Open MainViewModel.cs at RunTreeSearchAsync. Point to ResolveSearchRoots, the cancellation token, seen, FileIndexService.Covers, and DispatcherTimer.",
"This is the MainViewModel. It is the central controller for the screen because it coordinates navigation, folder loading, search, sidebar entries, tags, saved preferences, and status information. I am now looking at one method inside it called RunTreeSearchAsync. This method prepares and runs a search through the selected folder locations.<br/><br/>First, it gets the folders that should be searched by calling ResolveSearchRoots. Then it checks whether the user entered a search query and whether there are valid folders to search. If either one is missing, the method exits early so the application does not do unnecessary work.<br/><br/>Next, the code creates a cancellation token. Searching through folders can take time. If the user changes the search or navigates somewhere else, Clearspace can cancel the old search instead of continuing unnecessary work in the background.<br/><br/>The code then creates a HashSet called seen. This stores file paths that were already found, so the same file is not displayed more than once. The stopwatch tracks search time. The method also checks whether the in-memory file index already covers every folder being searched. If it does not, Clearspace marks that it is performing a deeper tree search. Finally, the DispatcherTimer updates results every 250 milliseconds. This lets the application publish results in groups instead of refreshing the screen for every single result, which helps the interface stay responsive.<br/><br/>This method shows the strength of MainViewModel: it coordinates the search workflow, cancellation, duplicate prevention, index coverage, and user-interface updates in one place. However, it also shows the limitation. One class has many reasons to change, which can make it harder to maintain and test.")

section(story, "10:00 to 12:30 - Software engineering: file operations", "Open Clearspace/Commands, then FileOperationService.cs. Point to Delete, Copy, Move, Rename, Run, and the Boolean return value.",
"I will now move from the MainViewModel to FileOperationService. The MainViewModel coordinates what the user wants to do, but this service performs the actual Windows file operations. This service is a wrapper around Windows Shell APIs. That keeps low-level Windows file-operation code out of the user interface.<br/><br/>The Delete method accepts a list of file paths, the owner window, and a setting that decides whether the deletion is permanent. If there are no paths, it returns false immediately. If the deletion is not permanent, the code uses the allow-undo flag, which lets Windows send the files to the Recycle Bin. Copy, Move, and Rename each pass their operation type, paths, destination, and flags into the shared Run method. That is a good design choice because the detailed setup code is not duplicated.<br/><br/>Inside Run, the code creates an SHFILEOPSTRUCT. This is the structure Windows requires before it performs a file operation. The helper named ToDoubleNullTerminated changes the list of file paths into the special null-separated format that the Windows Shell API expects. The service then calls SHFileOperationW. The current code returns true only when Windows reports success and the user did not cancel the operation.<br/><br/>The limitation is this Boolean result. A true-or-false answer does not explain why an operation failed. The user may have canceled it, Windows may have denied permission, a file may have been missing, or another Windows error may have occurred. These are different situations, but the method returns false for all of them. The ShowProperties and OpenWith methods use the same idea by calling another Windows API with the properties or open-with command.")

section(story, "12:30 to 15:00 - Finish software engineering", "Leave MainViewModel.cs or FileOperationService.cs open.",
"To summarize the software engineering and design category, Clearspace already has a strong foundation. It separates the user interface, commands, services, models, and Windows-specific code instead of putting all logic directly into the window. The MainViewModel coordinates important interface behavior, and FileOperationService keeps Windows shell work separate from the interface.<br/><br/>My planned enhancement will build on this design instead of replacing it. I will split the oversized MainViewModel into smaller focused responsibilities, including navigation coordination, search coordination, and sidebar state. I will replace simple Boolean file-operation results with a richer result object. That result will communicate whether an operation succeeded, was canceled, failed because of permissions, failed because a file was missing, or failed for another Windows reason. I will also add automated tests for search behavior, tag behavior, indexing edge cases, and file-operation failures. These changes will make Clearspace easier to maintain, easier to test, and more reliable when handling user files.<br/><br/>That concludes my software engineering and design review. I will now move to algorithms and data structures, beginning with the search experience.")

section(story, "15:00 to 16:30 - Algorithms: why search uses an index", "Return to Clearspace and run another simple search.",
"For the algorithms and data-structures category, I am returning to search. A user expects search to respond quickly even when a drive contains many files. Walking through every folder on disk every time the user types a search would be slow. Clearspace can use visible-folder results, an in-memory index, Windows Search, and a fallback directory crawl when needed. The important idea is that an index stores information that has already been collected so repeated searches can look through memory instead of walking the disk again. This is a tradeoff. The application uses memory and background work to build and maintain the index, but it can return repeated search results more quickly. Next, I will show the data structure that stores the indexed information.")

section(story, "16:30 to 19:30 - Algorithms: FileIndex", "Open FileIndex.cs. Point to IndexEntry, NameOffset, ParentIndex, character pools, folded names, and Search.",
"This file contains the in-memory data structure used for indexed search. Each IndexEntry stores information about a file, including its size, timestamps, attributes, a name offset, and a parent index. Instead of storing a full separate name string inside every entry, Clearspace stores file names in a shared character pool. The name offset tells the application where a file name starts in that pool. This saves memory when the application indexes a large number of files.<br/><br/>ParentIndex records the relationship between an item and its containing folder. When a result needs a full path, the application can follow those parent relationships instead of storing a complete repeated path in every entry. The folded-name pool keeps a normalized lowercase version of names. That means the application does not need to repeatedly convert every file name to lowercase every time the user changes the search query.<br/><br/>The Search method looks through these entries, applies the user's search filters, and can scan independent chunks in parallel. The tradeoff is clear: Clearspace uses more memory and background indexing work, but it can make repeated searches faster. The next file shows how this index is built by scanning the file system.")

section(story, "Minutes 19 to 22 - FileIndexBuilder", "Open FileIndexBuilder.cs. Point to MaxDepth = 32, the pending stack, and the reparse-point check.",
"FileIndexBuilder creates the in-memory index by walking directories in the background. It uses a stack to track directories that still need to be scanned. A stack supports a depth-first traversal. The builder takes one pending directory, records its files and folders, and adds safe child directories to the list of work that remains.<br/><br/>The code also avoids reparse points. This is important because symbolic links and Windows junctions can create cycles or lead the search outside the intended directory tree. Skipping them is a defensive choice that helps prevent the index builder from getting stuck in a loop.<br/><br/>However, there is a limitation near the top of the file. MaxDepth is set to 32. This means a file that is nested more than 32 folder levels below the starting location may not be added to the index. That can become a correctness problem because a user may expect search to include all files. The next fallback search path has a second depth limit, so I will compare it next.")

section(story, "22:00 to 24:00 - Algorithms: fallback search", "Open FileSearchService.cs. Point to MaxDepth = 24, worker tasks, queue, and batched results.",
"FileSearchService is the fallback search path for locations that are not completely covered by the in-memory index. It uses background workers to scan directories and publishes results in batches. This is useful because the user can begin seeing matches while additional folders are still being scanned.<br/><br/>The concern is that this fallback search has another hard-coded depth limit. In this file, MaxDepth is 24, which is different from the index builder limit of 32. That means the fallback search can include fewer nested folders than the main index. A user could receive different search coverage depending on which search path is used. The code is trying to be responsive, but the two different depth caps create a correctness and consistency concern.<br/><br/>My enhancement will make traversal depth configurable or remove arbitrary caps after I verify that cycle protection and cancellation remain safe. I will test both search paths with the same deep folder structures so the application gives more consistent results.")

section(story, "24:00 to 26:00 - Finish algorithms and data structures", "Return to FileIndex.cs and point to ParentIndex again.",
"My algorithms and data-structures enhancement has two parts. First, I will make traversal depth configurable or remove the arbitrary depth limits while keeping the safe handling of reparse points and cancellation. I will test deep folder structures, renamed files, deleted files, and canceled searches in both search paths.<br/><br/>Second, I will add folder-size aggregation to the index. Each indexed item already stores its size and its parent relationship. After an index build, Clearspace can work from the deepest items upward. Each file adds its size to its parent folder, and each folder adds its completed total to its own parent. This is called a bottom-up pass. It lets Clearspace show the total size of a folder without scanning the disk again every time the user asks for it.<br/><br/>I will measure memory use and indexing time, and I will add correctness tests for nested folders. The goal is not only fast search. The goal is search and folder information that users can trust. That concludes my algorithms and data-structures review. I will now move to the database category by returning to tags.")

section(story, "26:00 to 27:30 - Database: explain tags", "Return to Clearspace. Show a tag, tag menu, or tag-related search filter.",
"For the database category, I am returning to tags. Tags are user-managed metadata. A file can have multiple tags, and one tag can be used on many files. For example, one file could have both a School tag and a Portfolio tag, while the School tag can be attached to many different files. This is called a many-to-many relationship.<br/><br/>The user experience is simple, but the application has to store the relationship accurately as tags are added, removed, renamed, and deleted. The question for this category is whether the current storage design remains reliable as the user creates more tags and assignments over time. Next, I will show the current tag storage service.")

section(story, "27:30 to 28:00 - Open TagService", "Open TagService.cs and pause at the top of the file.",
"The tag feature is simple for the user, but the next file shows how Clearspace stores the tag definitions and file assignments behind the screen. I will now look at the current storage design and explain why I plan to improve it.")

plain_section(story, "TagService", "Open TagService.cs. Point to TagData, Definitions, Assignments, Load, Save, and Delete.",
"This service shows how tags are stored today. TagData contains a list of tag definitions and a dictionary that maps a file path to a list of tag IDs. The definitions represent the available tags. The assignment dictionary records which tag identifiers belong to each file path. This correctly represents the basic many-to-many relationship in application memory, and JSON is simple to use for a small amount of data.<br/><br/>However, each update rewrites the full JSON document. When a tag is deleted, the application manually searches assignments to remove tag IDs that no longer belong to a valid tag. This means relationship cleanup is handled by application code. It also means related changes are not protected by a database transaction. Data integrity means the stored data stays consistent. In this case, an assignment should not point to a tag that no longer exists. The current JSON approach is simple, but it becomes harder to manage as tags and assignments grow.")

section(story, "29:00 to 30:00 - Database enhancement and final close", "Leave TagService.cs open.",
"My planned database enhancement is a local SQLite database for tag metadata. I will use a Tags table for tag definitions and a FileTags table for assignments. The FileTags table will store the connection between a file path and a tag ID, which is the relational form of the many-to-many relationship.<br/><br/>I will enable foreign keys so an assignment cannot refer to a tag that does not exist. I will add an index for tag lookups, use parameterized queries, and perform related changes inside transactions. I will also migrate existing JSON data safely and keep a backup until the migration succeeds.<br/><br/>To conclude, Clearspace already has a strong foundation. My planned enhancements will make the application easier to maintain, more accurate when searching, and more reliable when storing tag data. Thank you for reviewing my code-review plan.")

doc = SimpleDocTemplate(str(OUT), pagesize=letter, leftMargin=0.65*inch, rightMargin=0.65*inch, topMargin=0.65*inch, bottomMargin=0.72*inch, title="Clearspace CS 499 Word-for-word Recording Script", author="Codex")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print(OUT)
