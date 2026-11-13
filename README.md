# Parallel Passages Editor

*This page is the English user guide, [ParallelPassagesEditor_UserGuide.rtf](ParallelPassagesEditor_UserGuide.rtf). The guide is also available in Spanish ([ParallelPassagesEditor_UserGuide_sp.rtf](ParallelPassagesEditor_UserGuide_sp.rtf)) and French ([ParallelPassagesEditor_UserGuide_fr.rtf](ParallelPassagesEditor_UserGuide_fr.rtf)).*

Paratext shows parallel passages (for example MAT 28:1 alongside MRK 16:1-2, LUK 24:1 and JHN 20:1) from a list in its file *ParallelPassages.xml*. This tool lets you hide whole books or chapters from that list, for example books your team has not drafted yet, and unhide them again later.

**Important:** ParallelPassages.xml is part of the Paratext program, not of a project. Anything you hide applies to **every project** on this computer.

## 1. Opening the tool

**From Paratext (plugin):** in any project's window, choose Tools > Parallel Passages Editor... The plugin must be installed once first (see below).

The tool always runs as administrator, because it changes a file in Program Files. Each time it starts, Windows asks for permission (click Yes, or enter an administrator password). If you say No, the tool does not open.

The tool opens Paratext's own file automatically:

```
C:\Program Files\Paratext 9\ParallelPassages\ParallelPassages.xml
```

If it is not there, the tool asks you to find it. File > Open lets you open a different copy at any time. The blue bar at the top always shows which file is open.

**Installing the plugin:** download Install_Phils_tools.exe from the Paratext_Plugins folder (if your browser warns about the file, choose to keep it) and close Paratext. Double-click Install_Phils_tools.exe. If Windows says it protected your PC, click “More info”, then “Run anyway” (the installer is not signed yet). Tick Parallel Passages Editor and click “Install / update ticked”. Windows asks for administrator permission; click Yes. The installer puts the plugin into every Paratext 9 installation on the computer, in plugins\ParallelPassagesEditor, and checks that Paratext can load it. The editor program itself goes into C:\Program Files\ParallelPassagesEditor, a folder that only administrators can change, so that nobody can replace the program that runs as administrator. The same window later shows “Update available” when there is a newer version, updates the plugin, removes it (“Remove ticked”), and opens this guide (“User guide”). The plugin was built for Paratext 9.5 and may not load in older versions.

## 2. The three lists

**Books** (left): every book that has parallel passages, in Bible order. Each row shows how many of its entries are hidden, for example `12 / 40 hidden`.

**Chapters** (middle): the chapters of the selected book that have parallel passages.

**Entries** (right): every entry in the selected book or chapters, with the passages it is parallel to and whether it is hidden. This list is for information only; it shows exactly what a book or chapter checkbox affects.

When the tool opens, nothing is selected. Click a book to see its chapters and all its entries, then click a chapter to see only that chapter's entries. The selected book and chapter have a dark blue border. The Hide and Unhide buttons work only on what you have selected, and stay greyed out until you select something.

A checkbox shows the state of a book or chapter: ticked means everything in it is hidden, empty means nothing is hidden, and a filled square means only some entries are hidden.

## 3. Hiding and unhiding

**Tick a book or chapter** to hide it, and untick it to show it again. Clicking a partly hidden book or chapter hides all of it.

You can also select several books or chapters (Ctrl+click or Shift+click) and use the buttons: **Hide book**, **Unhide book**, **Hide chapter(s)**, **Unhide chapter(s)**. **Unhide all** shows everything again.

A hidden entry only disappears where that book is referred to. For example, hiding JHN removes JHN 20:1 from the MAT 28:1 passage, but MAT, MRK and LUK still show each other.

### Passages with fewer than 2 entries left are hidden entirely

A parallel needs at least 2 entries to compare, and a passage left with no entries, or only one, can stop Paratext from starting. So when hiding would leave a passage with fewer than 2 entries, the tool hides the **whole passage**, including its `<Passage>` and `</Passage>` lines. For example, with JHN hidden, the passage pairing PSA 22:19 with JHN 19:24 becomes:

```
<!--  <Passage> -->
<!--    <Verse HEB="222225222222">PSA 22:19</Verse> (with passage) -->
<!--    <Verse GRK="...">JHN 19:24</Verse> -->
<!--  </Passage> -->
```

A passage made up only of John (such as JHN 6:32, 6:33, 6:41, 6:50, 6:51 and 6:58) is hidden entirely in the same way. Passages that still have 2 or more entries left only lose the hidden book's entries.

The note `(with passage)` marks entries of other books that are hidden only because their passage is. Paratext ignores it, and it lets the tool bring them back: when you unhide John, the PSA 22:19 passage comes back exactly as it was.

PSA stays unticked, because you did not hide it, but its count shows the entries hidden with their passage, for example `5 / 230 hidden, 5 with passage`. In the Entries list they are shown in brown italics with a note explaining why, and the status bar tells you how many passages are hidden entirely.

A passage can contain several books, so hiding one book can change another: if you hide LUK as well as JHN, a MAT/LUK/JHN passage would be left with only MAT, so that whole passage is hidden.

If you open a file that has a passage with fewer than 2 visible entries (for example one saved by an earlier version of this tool), the tool tells you and hides those passages entirely when you save.

## 4. Saving

Use File > Save (Ctrl+S). Then **restart Paratext** so it reads the changed file.

The first time you save, the tool keeps one copy of the file as it was, next to it: `ParallelPassages.xmlorg`. After that it makes no more copies, and it never changes or replaces `ParallelPassages.xmlorg` (so a copy you made yourself under that name is kept as it is).

To go back to the original, copy `ParallelPassages.xmlorg` and rename the copy to `ParallelPassages.xml`, or use Unhide all and save.

## 5. What changes in the file

Hiding an entry turns its line into an XML comment, which Paratext ignores. For example:

```
<Verse GRK="...">JHN 20:1</Verse>
becomes
<!--    <Verse GRK="...">JHN 20:1</Verse> -->
```

Unhiding removes the comment marks again. No other line in the file is changed. Entries you commented out by hand are recognized as hidden.

## 6. After a Paratext update

A Paratext update or reinstall may replace ParallelPassages.xml and bring back everything you hid. The tool remembers your hidden books, chapters and entries each time you save. When it opens a file with nothing hidden, it offers to reapply that list. You can also do this at any time with File > Reapply saved hidden list. The tool also remembers the passages you added. When some are missing from the file, it offers to add them again; **File > Add back my saved passages** does the same at any time. Then save as usual.

## 7. Saving a list for each project

When you open the tool from a project (with the plugin's Tools menu), the blue bar shows the project's name, and a checkbox appears near the top: **Save this hidden list for project PDT15**.

While it is ticked, every Save also keeps a copy of the hidden list in that project's folder, in `local\ParallelPassagesEditor\hidden.txt`. Untick it and save to delete the project's copy. The box is ticked automatically when the project already has a saved list. The project's copy is written or deleted when you close the editor, so keep Paratext open until then.

The next time you open the tool from that project, it checks whether ParallelPassages.xml still matches the project's list. If not (for example after you used another project's list, or after a Paratext update), it asks whether to use the project's list. Answer Yes, then press Save and **restart Paratext**.

Remember that ParallelPassages.xml is shared by all projects on the computer: only one list can be in effect at a time. Paratext's Send/Receive does not copy the `local` folder, so each computer keeps its own project lists.

## 8. Adding your own parallel passages

Paratext's list may lack a parallel your team wants to compare. Below the Entries list, click **Add passage...** and type the references of the new passage, one per line, at least two, for example GEN 2:24 and MAT 19:5. Write them as in Paratext's list (MRK 10:7-8, 2CH 8:13,16), in the Original versification that Paratext uses for this list: for example, Jonah's prayer starts at JON 2:1, not 1:17. The window tells you at once about a reference that is not valid or does not exist, a reference typed twice, or a passage that is already in the file.

Passages you add are shown in blue with the note **Added by you**, and the status bar counts them. Select one of their entries to use **Edit passage...** (or double-click the entry) or **Remove passage**. Only passages you added can be changed or removed; Paratext's own passages can only be hidden. Hiding works the same way for both.

Paratext's list also records which words of the verses match, and the Parallel Passages tool highlights them. The tool cannot work this out, so the words of passages you add are not highlighted, but Paratext still shows the verses side by side. As with hiding, save and then restart Paratext. Passages you add appear in every project on this computer.

In the file, each passage you add is written just before the end, after the comment &lt;!-- Added by Parallel Passages Editor -->, with an empty word list (HEB="" or GRK="").

## 9. Changing the interface language

The Language dropdown near the top switches the tool's menus, labels and messages between English, Spanish, French, Portuguese and Chinese (Simplified). Your choice is remembered.
