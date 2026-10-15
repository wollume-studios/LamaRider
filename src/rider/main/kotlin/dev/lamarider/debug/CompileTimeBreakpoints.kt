package dev.lamarider.debug

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.runWriteAction
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VfsUtilCore
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugSessionListener
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.XDebuggerManagerListener
import com.intellij.xdebugger.breakpoints.XBreakpoint
import com.intellij.xdebugger.breakpoints.XBreakpointManager
import com.intellij.xdebugger.breakpoints.XBreakpointProperties
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import com.intellij.xdebugger.breakpoints.XLineBreakpointType
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Breakpoints in aspects, templates and fabrics: the debugger only knows Metalama's compiled copy of that code (see
 * [CompileTimeMaps]), so for each debug session they are mirrored to the copies and removed after. An aspect test run
 * extracts its own copy shortly before running it; the session watches for it.
 */
class CompileTimeBreakpoints(private val project: Project) : XDebuggerManagerListener {
    private val log = logger<CompileTimeBreakpoints>()

    override fun processStarted(debugProcess: XDebugProcess) {
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val sources = manager.allBreakpoints.filterIsInstance<XLineBreakpoint<*>>()
            .filter { it.isEnabled && it.fileUrl.endsWith(".cs") }
            .groupBy { File(VfsUtilCore.urlToPath(it.fileUrl)).path }
        if (sources.isEmpty()) return
        val root = CompileTimeMaps.root()
        val cache = File(root, "CompileTime")
        val mirrors = CopyOnWriteArrayList<XBreakpoint<*>>()

        fun mirror(copies: List<CompileTimeMaps.Copy>) {
            val targets = copies.flatMap { copy ->
                sources[File(copy.source).path].orEmpty().mapNotNull { source -> copy.line(source.line)?.let { Triple(source, copy.file.path, it) } }
            }
            if (targets.isEmpty()) return
            ApplicationManager.getApplication().invokeAndWait {
                runWriteAction {
                    for ((source, path, line) in targets) {
                        val file = LocalFileSystem.getInstance().refreshAndFindFileByPath(path) ?: continue
                        mirror(manager, source, file, line)?.let(mirrors::add)
                    }
                }
            }
            log.info("LamaRider: mirrored ${targets.size} breakpoints to ${copies.map { it.file.path }.distinct()}")
        }

        mirror(sources.keys.flatMap { CompileTimeMaps.copiesOf(cache, it) })

        val running = java.util.concurrent.atomic.AtomicBoolean(true)
        val started = System.currentTimeMillis()
        ApplicationManager.getApplication().executeOnPooledThread { try {
            // ponytail: polls (the copy is compiled ~50 ms after it is written); a miss means no stop, as without LamaRider.
            val builds = CompileTimeMaps.byName(cache)
            log.info("LamaRider: watching ${File(root, "Tests")} for ${builds.keys}")
            val done = HashSet<String>()
            val firstSeen = HashMap<String, Long>()
            while (running.get()) {
                File(root, "Tests").listFiles()?.filter { it.lastModified() >= started - 1000 }?.forEach { run ->
                    val found = File(run, "CompileTime").walk().maxDepth(4)
                        .filter { it.isFile && it.extension == "cs" && it.name in builds && it.path !in done }
                        .mapNotNull { file ->
                            log.info("LamaRider: test run copy ${file.path}")
                            // Read while still being written: try again until it matches, for a while.
                            val copy = CompileTimeMaps.mapOf(file, builds) { it in sources }
                            if (copy != null || System.currentTimeMillis() - firstSeen.getOrPut(file.path) { System.currentTimeMillis() } > 2000) done += file.path
                            copy
                        }
                        .toList()
                    if (found.isNotEmpty()) mirror(found)
                }
                Thread.sleep(20)
            }
        } catch (e: Exception) { log.warn("LamaRider: watching for compile-time copies failed", e) } }
        debugProcess.session.addSessionListener(object : XDebugSessionListener {
            override fun sessionStopped() {
                running.set(false)
                ApplicationManager.getApplication().invokeLater { runWriteAction { mirrors.forEach(manager::removeBreakpoint) } }
            }
        })
    }

    @Suppress("UNCHECKED_CAST")
    private fun mirror(manager: XBreakpointManager, source: XLineBreakpoint<*>, file: VirtualFile, line: Int): XBreakpoint<*>? {
        val type = source.type as XLineBreakpointType<XBreakpointProperties<*>>
        if (manager.findBreakpointsAtLine(type, file, line).isNotEmpty()) return null // already there (the user's own)
        return manager.addLineBreakpoint(type, file.url, line, type.createBreakpointProperties(file, line)).also {
            it.conditionExpression = source.conditionExpression
        }
    }
}
