package dev.lamarider

/** One aspect in the Code Vision popup. Attribute aspects need no "Go to" (Ctrl+click on the attribute does that). */
data class AspectEntry(val instance: AspectInstanceInfo, val name: String) {
    val viaAttribute get() = instance.appliedVia == "Attribute"
    val label get() = if (viaAttribute) name else "$name (${origin(instance.appliedVia)})"

    companion object {
        /** Same wording as the backend's hints: "fabric", "inherited", "child aspect". */
        fun origin(appliedVia: String) = when (appliedVia) {
            "ChildAspect" -> "child aspect"
            "RequiredAspect" -> "required aspect"
            "" -> "unknown"
            else -> appliedVia.lowercase()
        }
    }
}

/** The aspects on one declaration: the member, one of its parameters, or its return value. */
data class TargetAspects(val caption: String, val entries: List<AspectEntry>)

/**
 * The popup's groups for a Code Vision click: a member id shows the member, its parameters and return value;
 * a parameter id ("M:...;Parameter=0", from the parameter's inlay hint) only that parameter.
 */
fun ModelData.aspectsByTarget(docId: String): List<TargetAspects> {
    val instances = if (';' in docId) instances.filter { it.target == docId } else instancesOn(docId)
    return instances.distinctBy { Triple(it.target, it.aspectClass, it.appliedVia) }
        .groupBy { it.target }
        .entries.sortedBy { (target, _) -> order(target) }
        .map { (target, group) -> TargetAspects(caption(target, group[0]), group.map { AspectEntry(it, aspectClass(it.aspectClass)?.name ?: it.aspectClass.substringAfterLast('.').removePrefix("T:")) }) }
}

private fun order(target: String) = when {
    ";Parameter=" in target -> 1 + (target.substringAfter(";Parameter=").toIntOrNull() ?: 0)
    ';' in target -> Int.MAX_VALUE // return value
    else -> 0
}

private fun caption(target: String, instance: AspectInstanceInfo) = when {
    ";Parameter=" in target -> "Parameter " + instance.memberName.substringAfterLast(" › ")
    ';' in target -> "Return value"
    else -> instance.memberName
}
