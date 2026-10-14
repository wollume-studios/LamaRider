package dev.lamarider.explorer

import com.intellij.icons.AllIcons
import com.intellij.ide.BrowserUtil
import com.intellij.ide.DefaultTreeExpander
import com.intellij.ide.actions.CollapseAllAction
import com.intellij.ide.actions.ExpandAllAction
import com.intellij.openapi.Disposable
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.SimpleToolWindowPanel
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.text.StringUtil
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.ui.ColoredTreeCellRenderer
import com.intellij.ui.DocumentAdapter
import com.intellij.ui.OnePixelSplitter
import com.intellij.ui.SearchTextField
import com.intellij.ui.SimpleTextAttributes
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.treeStructure.Tree
import com.intellij.util.ui.JBUI
import com.intellij.util.ui.UIUtil
import com.intellij.util.ui.tree.TreeUtil
import dev.lamarider.AspectClassInfo
import dev.lamarider.AspectGroup
import dev.lamarider.AspectInstanceInfo
import dev.lamarider.DiagnosticInfo
import dev.lamarider.LamaRiderService
import dev.lamarider.ModelData
import dev.lamarider.SourceLocation
import dev.lamarider.TransformationInfo
import dev.lamarider.diff.MetalamaDiff
import java.awt.BorderLayout
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import javax.swing.Icon
import javax.swing.JEditorPane
import javax.swing.JPanel
import javax.swing.JTree
import javax.swing.event.DocumentEvent
import javax.swing.event.HyperlinkEvent
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeModel
import javax.swing.tree.TreePath

/**
 * Top: aspect classes grouped by project/package. Middle: documentation of the selected aspect.
 * Bottom: its targets as Project › Namespace › Type › Member, with transformations and diagnostics.
 */
class AspectExplorerPanel(private val project: Project, parent: Disposable) : SimpleToolWindowPanel(true, true) {
    private val service = project.service<LamaRiderService>()
    private val search = SearchTextField(false)
    private val classRoot = DefaultMutableTreeNode()
    private val classTree = Tree(DefaultTreeModel(classRoot))
    private val details = JEditorPane().apply {
        contentType = "text/html"
        isEditable = false
        border = JBUI.Borders.empty(6, 8)
        putClientProperty(JEditorPane.HONOR_DISPLAY_PROPERTIES, true)
        font = UIUtil.getLabelFont()
    }
    private val targetRoot = DefaultMutableTreeNode()
    private val targetTree = Tree(DefaultTreeModel(targetRoot))
    private val status = JBLabel()
    private var shownModel: ModelData? = null

    init {
        classTree.isRootVisible = false
        classTree.showsRootHandles = true
        classTree.cellRenderer = ClassRenderer()
        classTree.addTreeSelectionListener { showSelection() }
        search.addDocumentListener(object : DocumentAdapter() {
            override fun textChanged(e: DocumentEvent) = rebuildClasses()
        })

        targetTree.isRootVisible = false
        targetTree.cellRenderer = TargetRenderer()
        targetTree.addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) {
                if (e.clickCount == 2) activate((targetTree.lastSelectedPathComponent as? DefaultMutableTreeNode)?.userObject)
            }
        })

        details.addHyperlinkListener { e ->
            if (e.eventType != HyperlinkEvent.EventType.ACTIVATED) return@addHyperlinkListener
            val target = e.description ?: return@addHyperlinkListener
            if (target.startsWith("source:")) selectedClass()?.let(::goToSource)
            else BrowserUtil.browse(target)
        }

        val top = JPanel(BorderLayout()).apply {
            add(search, BorderLayout.NORTH)
            add(JBScrollPane(classTree), BorderLayout.CENTER)
        }
        val bottom = OnePixelSplitter(true, 0.3f).apply {
            firstComponent = JBScrollPane(details)
            secondComponent = JBScrollPane(targetTree)
        }
        val splitter = OnePixelSplitter(true, 0.45f).apply {
            firstComponent = top
            secondComponent = bottom
        }
        setContent(JPanel(BorderLayout()).apply {
            add(splitter, BorderLayout.CENTER)
            add(status, BorderLayout.SOUTH)
        })

        val expander = DefaultTreeExpander(targetTree)
        val actions = DefaultActionGroup(
            ActionManager.getInstance().getAction("LamaRider.Refresh"),
            ExpandAllAction { expander },
            CollapseAllAction { expander },
        )
        toolbar = ActionManager.getInstance().createActionToolbar("AspectExplorer", actions, true)
            .apply { targetComponent = this@AspectExplorerPanel }.component

        service.addListener(parent) { onServiceChanged() }
        current = this
        Disposer.register(parent) { if (current === this) current = null }
        onServiceChanged()
    }

    fun select(aspectClassId: String) {
        search.text = ""
        rebuildClasses()
        val node = TreeUtil.findNode(classRoot) { (it.userObject as? AspectClassInfo)?.id == aspectClassId } ?: return
        val path = TreePath(node.path)
        classTree.expandPath(path.parentPath)
        classTree.selectionPath = path
        classTree.scrollPathToVisible(path)
    }

    private fun onServiceChanged() {
        status.text = service.status
        status.toolTipText = service.status
        if (service.model !== shownModel) {
            shownModel = service.model
            rebuildClasses()
        }
    }

    private fun selectedClass(): AspectClassInfo? =
        (classTree.lastSelectedPathComponent as? DefaultMutableTreeNode)?.userObject as? AspectClassInfo

    private fun rebuildClasses() {
        val selected = selectedClass()?.id
        val filter = search.text.trim()
        classRoot.removeAllChildren()
        for (group in service.model?.groups(filter).orEmpty()) {
            val groupNode = DefaultMutableTreeNode(group).also(classRoot::add)
            group.classes.forEach { groupNode.add(DefaultMutableTreeNode(it)) }
        }
        (classTree.model as DefaultTreeModel).reload()
        // Projects open, packages closed (expanded while searching).
        for (child in classRoot.children()) {
            val node = child as DefaultMutableTreeNode
            if (filter.isNotEmpty() || (node.userObject as AspectGroup).isProject) classTree.expandPath(TreePath(node.path))
        }
        if (selected != null) {
            TreeUtil.findNode(classRoot) { (it.userObject as? AspectClassInfo)?.id == selected }?.let { classTree.selectionPath = TreePath(it.path) }
        }
        showSelection()
    }

    private fun showSelection() {
        val aspect = selectedClass()
        details.text = aspect?.let(::detailsHtml) ?: "<html><body><span style=\"color:gray\">Select an aspect to see its documentation and targets.</span></body></html>"
        details.caretPosition = 0
        rebuildTargets(aspect)
    }

    private fun goToSource(aspect: AspectClassInfo) {
        if (aspect.location != null) Navigation.open(project, aspect.location) else service.navigateToClass(aspect.id)
    }

    private fun detailsHtml(aspect: AspectClassInfo): String {
        val links = buildList {
            if (aspect.location != null || aspect.isLibrary) add("<a href=\"source:\">Go to source</a>")
            aspect.docUrl?.let { add("<a href=\"$it\">Online documentation</a>") }
        }
        val summary = aspect.summary?.let { StringUtil.escapeXmlEntities(it) } ?: "<i>No documentation.</i>"
        return "<html><body><b>${StringUtil.escapeXmlEntities(aspect.name)}</b> &nbsp;<span style=\"color:gray\">" +
            "${StringUtil.escapeXmlEntities(aspect.group)}</span>" +
            (if (links.isEmpty()) "" else "<br>" + links.joinToString(" &nbsp; ")) + "<br>$summary</body></html>"
    }

    // Project › Namespace › Type › Member, children: transformations + diagnostics
    private fun rebuildTargets(aspect: AspectClassInfo?) {
        targetRoot.removeAllChildren()
        val model = service.model
        if (model != null && aspect != null) {
            for ((projectName, inProject) in model.instancesOf(aspect.id).groupBy { it.project }.toSortedMap()) {
                val projectNode = DefaultMutableTreeNode(Container(projectName, AllIcons.Nodes.Module)).also(targetRoot::add)
                for ((ns, inNs) in inProject.groupBy { it.targetType.substringBeforeLast('.', "<global>") }.toSortedMap()) {
                    val nsNode = DefaultMutableTreeNode(Container(ns, AllIcons.Nodes.Package)).also(projectNode::add)
                    for ((type, inType) in inNs.groupBy { it.targetType.substringAfterLast('.') }.toSortedMap()) {
                        val typeNode = DefaultMutableTreeNode(Container(type, AllIcons.Nodes.Class)).also(nsNode::add)
                        for (instance in inType.sortedBy { it.targetName }) {
                            val node = DefaultMutableTreeNode(TargetNode(instance)).also(typeNode::add)
                            model.transformationsOf(instance).forEach { node.add(DefaultMutableTreeNode(TransformationNode(it))) }
                            model.diagnosticsOn(instance.target).forEach { node.add(DefaultMutableTreeNode(DiagnosticNode(it))) }
                        }
                    }
                }
            }
        }
        (targetTree.model as DefaultTreeModel).reload()
        TreeUtil.expandAll(targetTree)
    }

    private fun activate(node: Any?) {
        when (node) {
            is TargetNode -> Navigation.open(project, node.instance.location)
            is TransformationNode -> node.transformation.location?.let { MetalamaDiff.open(project, it.file, it.start) }
            is DiagnosticNode -> node.diagnostic.file?.let { Navigation.openLine(project, it, (node.diagnostic.line ?: 1) - 1) }
        }
    }

    private inner class ClassRenderer : ColoredTreeCellRenderer() {
        override fun customizeCellRenderer(tree: JTree, value: Any?, selected: Boolean, expanded: Boolean, leaf: Boolean, row: Int, hasFocus: Boolean) {
            when (val item = (value as? DefaultMutableTreeNode)?.userObject) {
                is AspectGroup -> {
                    append(item.name, SimpleTextAttributes.REGULAR_BOLD_ATTRIBUTES)
                    append("  ${if (item.isProject) "project" else "package"} · ${item.classes.size}", SimpleTextAttributes.GRAYED_ATTRIBUTES)
                }
                is AspectClassInfo -> {
                    val count = service.model?.instancesOf(item.id)?.size ?: 0
                    append(item.name, if (count > 0) SimpleTextAttributes.REGULAR_ATTRIBUTES else SimpleTextAttributes.GRAYED_ATTRIBUTES)
                    if (count > 0) append("  $count", SimpleTextAttributes.GRAYED_ATTRIBUTES)
                }
            }
        }
    }

    private class TargetRenderer : ColoredTreeCellRenderer() {
        override fun customizeCellRenderer(tree: JTree, value: Any?, selected: Boolean, expanded: Boolean, leaf: Boolean, row: Int, hasFocus: Boolean) {
            when (val item = (value as? DefaultMutableTreeNode)?.userObject) {
                is Container -> { icon = item.icon; append(item.name) }
                is TargetNode -> {
                    icon = kindIcon(item.instance.targetKind)
                    append(item.name)
                    append("  ${kindLabel(item.instance.targetKind)} · ${item.instance.appliedVia}", SimpleTextAttributes.GRAYED_ATTRIBUTES)
                }
                is TransformationNode -> { icon = AllIcons.Actions.Diff; append(item.transformation.description) }
                is DiagnosticNode -> {
                    icon = if (item.diagnostic.severity == "Error") AllIcons.General.Error else AllIcons.General.Warning
                    append("${item.diagnostic.id}: ${item.diagnostic.message}")
                }
            }
        }
    }

    private class Container(val name: String, val icon: Icon)

    private class TargetNode(val instance: AspectInstanceInfo) {
        /** "Add(int, int)" rather than "Calculator.Add(int, int)": the type is the parent node. */
        val name: String get() = instance.targetName.removePrefix(instance.targetType.substringAfterLast('.') + ".").replace("/", " › ")
    }

    private class TransformationNode(val transformation: TransformationInfo)

    private class DiagnosticNode(val diagnostic: DiagnosticInfo)

    companion object {
        // Metalama DeclarationKind names.
        fun kindIcon(kind: String): Icon = when (kind) {
            "NamedType" -> AllIcons.Nodes.Class
            "Method", "Constructor", "Finalizer", "Operator" -> AllIcons.Nodes.Method
            "Property", "Indexer", "Event" -> AllIcons.Nodes.Property
            "Field" -> AllIcons.Nodes.Field
            "Parameter" -> AllIcons.Nodes.Parameter
            "Namespace" -> AllIcons.Nodes.Package
            "Compilation" -> AllIcons.Nodes.Module
            else -> AllIcons.Nodes.Unknown
        }

        fun kindLabel(kind: String) = when (kind) {
            "NamedType" -> "type"
            "Compilation" -> "project"
            else -> kind.lowercase()
        }

        var current: AspectExplorerPanel? = null
            private set
    }
}

object Navigation {
    fun open(project: Project, location: SourceLocation?) {
        val file = location?.let { LocalFileSystem.getInstance().findFileByPath(it.file) } ?: return
        OpenFileDescriptor(project, file, location.start).navigate(true)
    }

    fun openLine(project: Project, path: String, line: Int) {
        val file = LocalFileSystem.getInstance().findFileByPath(path) ?: return
        OpenFileDescriptor(project, file, line.coerceAtLeast(0), 0).navigate(true)
    }
}
