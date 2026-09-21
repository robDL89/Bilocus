# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Approximation of the selected edges with arcs and lines, for the
# "Arcs and lines" mode of Create proxy. This module must NOT import bpy:
# it is shared with the tests, like bridge_edges.py.
#
# The problem: a curve modeled in Blender arrives as a chain of short
# edges. In Polyline mode each one becomes a straight ModelCurve, and a beam
# traced with Pick Lines on that chain becomes a hundred short beams. Here
# the chain is rewritten with the fewest segments that stay within a
# maximum deviation from the original vertices: lines where the vertices
# are aligned, arcs where they sit on a circle, the original segment where
# neither holds up.
#
# What it deliberately does NOT do:
# - it does not impose tangency between consecutive segments. The endpoints
#   of every segment are original vertices, so the chain stays connected;
#   tangency emerges on its own the denser the mesh is, but it is not a
#   constraint.
# - it does not move the endpoints of the segments: a vertex shared with
#   another proxy or another chain stays where it is, and the chains stay
#   attached to each other.
# - it does not produce splines. Revit beams are traced with Pick Lines
#   only on lines and arcs; the NURBS case is a separate, later piece of
#   work.
#
# Arcs sit ONLY on two planes passing through their chord, because a beam
# picked up with Pick Lines orients its section on the curve's plane:
# - "plan": contains the chord and the horizontal perpendicular to it.
#   Tilted only as much as the chord's own slope, the same rule as lines.
# - "section": contains the chord and the vertical. For arcs in elevation.
# An arc on a plane free in space follows the curve's osculating plane,
# which where the curve is nearly straight and descending turns randomly:
# measured in the field on 2026-09-16, a 114 m radius arc on a descending
# segment had its plane tilted 40 degrees and the beam rolled with it. If
# the vertices do not sit on either plane, a shorter arc or a line wins. An
# arc on a free plane is a possible future third option (TODO).

import math

# The minimum deviation, in millimeters, used when the user leaves 0.
#
# A literal zero is not an approximation: with floats no vertex sits
# exactly on a line or a circle, and the result would be identical to
# Polyline. The useful "smallest possible" is the one that absorbs the
# numeric noise of a hand-drawn mesh and little else. 0.1 mm is under
# Revit's ShortCurveTolerance (about 0.8 mm) and above the float32
# resolution on the wire for coordinates within a few hundred meters of the
# internal origin, which is where the bridge works by decision.
MIN_TOLERANCE_MM = 0.1

# Maximum sweep of an arc, in radians. An arc beyond half a turn is
# legitimate for Revit but rare as a beam, and above all it bounds the
# pathological case of the closed chain: a full circle splits into two arcs
# instead of attempting one 360-degree arc with coincident start and end.
MAX_SWEEP = math.pi

# Maximum radius of an arc, in meters. Beyond this value three "almost
# aligned" points give a very distant, numerically fragile center: a line
# is the right answer, and if the line does not hold up the original
# segment remains.
MAX_RADIUS = 1000.0

# Below this length two points are treated as coincident. It only serves to
# avoid dividing by zero on closed chains, where the last vertex is the
# first.
_EPSILON = 1e-12


def resolve_tolerance(millimeters):
    """(meters, minimum_applied). The deviation in meters to use in the
    fitting.

    A zero deviation becomes MIN_TOLERANCE_MM and the second value says so,
    so the panel can warn instead of silently changing it. Negative or
    non-finite is a caller defect: the panel field has min=0."""
    value = float(millimeters)
    if not math.isfinite(value) or value < 0.0:
        raise ValueError("invalid deviation: {}".format(millimeters))
    if value < MIN_TOLERANCE_MM:
        return MIN_TOLERANCE_MM / 1000.0, True
    return value / 1000.0, False


# --- vectors ------------------------------------------------------------------

def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _scale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _length(a):
    return math.sqrt(_dot(a, a))


# --- chains -------------------------------------------------------------------

def build_chains(edges):
    """Orders the edges into continuous chains of points.

    `edges` are pairs of points already filtered by
    bridge_edges.prepare_edges (no doubles, no degenerates). Two edges are
    connected when they share an IDENTICAL point: this is the case for
    edges read from the same mesh, where the same vertex goes through the
    same transform and gives the same floats.

    A chain breaks at every node that does not have exactly two edges: free
    endpoints, crossings and branches. An arc crossing an intersection
    would join two branches the user sees as distinct.

    Returns a list of lists of points. A closed chain has its first point
    equal to its last. The order is deterministic, given the order of the
    edges: two sends of the same selection produce the same segments."""
    adjacency = {}
    order = []
    for index, (start, end) in enumerate(edges):
        for point, other in ((start, end), (end, start)):
            if point not in adjacency:
                adjacency[point] = []
                order.append(point)
            adjacency[point].append((index, other))

    used = [False] * len(edges)
    chains = []

    def walk(first, edge_index, other):
        points = [first, other]
        used[edge_index] = True
        current = other
        while len(adjacency[current]) == 2:
            following = None
            for candidate, neighbour in adjacency[current]:
                if not used[candidate]:
                    following = (candidate, neighbour)
                    break
            if following is None:
                break
            used[following[0]] = True
            current = following[1]
            points.append(current)
        return points

    # Open chains first, starting from nodes that are not pass-through:
    # starting from an internal node would split into two a chain that is
    # actually one.
    for point in order:
        if len(adjacency[point]) == 2:
            continue
        for edge_index, other in adjacency[point]:
            if not used[edge_index]:
                chains.append(walk(point, edge_index, other))

    # What remains are closed loops, made only of pass-through nodes.
    for index, (start, end) in enumerate(edges):
        if not used[index]:
            chains.append(walk(start, index, end))

    return chains


# --- fit checks --------------------------------------------------

def _line_fits(points, i, j, tolerance):
    """True if the vertices between i and j sit within `tolerance` of the
    line i-j, and in the same order along it.

    Order matters: a chain that goes forward and comes back on the same
    line would sit within tolerance, but rewriting it as a single segment
    would erase the return trip."""
    start = points[i]
    chord = _sub(points[j], start)
    length = _length(chord)
    if length <= _EPSILON:
        return False
    direction = _scale(chord, 1.0 / length)

    slack = tolerance
    previous = 0.0
    for k in range(i + 1, j):
        offset = _sub(points[k], start)
        along = _dot(offset, direction)
        if along < previous - slack or along > length + slack:
            return False
        across = _sub(offset, _scale(direction, along))
        if _length(across) > tolerance:
            return False
        previous = max(previous, along)
    return True


def circle_through(start, middle, end):
    """(center, radius, normal) of the circle through three points, or None
    if they are aligned or coincident.

    The normal is oriented so that, going counter-clockwise around it, from
    `start` one meets `middle` first and then `end`: this is what lets the
    angles of intermediate vertices be measured in the right direction."""
    a = _sub(middle, start)
    b = _sub(end, start)
    normal = _cross(a, b)
    normal_squared = _dot(normal, normal)
    scale = max(_dot(a, a), _dot(b, b))
    if scale <= _EPSILON or normal_squared <= 1e-18 * scale * scale:
        return None

    # center = start + (|a|^2 (b x n) + |b|^2 (n x a)) / (2 |n|^2)
    term = _add(_scale(_cross(b, normal), _dot(a, a)),
                _scale(_cross(normal, a), _dot(b, b)))
    center = _add(start, _scale(term, 0.5 / normal_squared))
    radius = _length(_sub(start, center))
    unit_normal = _scale(normal, 1.0 / math.sqrt(normal_squared))
    return center, radius, unit_normal


def _angle_on(center, normal, u_axis, v_axis, point):
    offset = _sub(point, center)
    angle = math.atan2(_dot(offset, v_axis), _dot(offset, u_axis))
    if angle < 0.0:
        angle += 2.0 * math.pi
    return angle


# Below this horizontal component the chord is treated as vertical: twin of
# ProxyPlaneNormal.MinHorizontal on the Revit side.
_MIN_HORIZONTAL = 1e-6


def _unit(a):
    length = _length(a)
    if length <= _EPSILON:
        return None
    return _scale(a, 1.0 / length)


def arc_plane_normals(start, middle, end):
    """The normals of the planes allowed for an arc from start to end, in
    the order they are tried: "plan" first, then "section".

    `middle` is only used for the vertical chord, where neither plane is
    defined by the chord alone: there, only the vertical plane through the
    midpoint remains."""
    direction = _unit(_sub(end, start))
    if direction is None:
        return []

    horizontal = math.sqrt(direction[0] * direction[0] + direction[1] * direction[1])
    if horizontal < _MIN_HORIZONTAL:
        offset = _sub(middle, start)
        side = _unit((offset[0], offset[1], 0.0))
        if side is None:
            return []
        return [_unit(_cross((0.0, 0.0, 1.0), side))]

    # plan: Z minus its projection onto the chord
    dz = direction[2]
    plan = _unit((-direction[0] * dz, -direction[1] * dz, 1.0 - dz * dz))
    # section: perpendicular to the chord and to the vertical
    section = _unit((direction[1], -direction[0], 0.0))
    return [normal for normal in (plan, section) if normal is not None]


def _arc_in_plane(points, i, j, normal):
    """(center, radius, normal, sweep, u, v, midpoint) of the arc from i to
    j in the plane through the chord with the given normal, passing through
    the plane projection of the middle vertex. None if not constructible."""
    start = points[i]
    middle = points[(i + j) // 2]
    projected = _sub(middle, _scale(normal, _dot(_sub(middle, start), normal)))

    circle = circle_through(start, projected, points[j])
    if circle is None:
        return None
    center, radius, oriented = circle
    if radius > MAX_RADIUS:
        return None

    u_axis = _scale(_sub(start, center), 1.0 / radius)
    v_axis = _cross(oriented, u_axis)
    sweep = _angle_on(center, oriented, u_axis, v_axis, points[j])
    if sweep > MAX_SWEEP + 1e-9:
        return None

    half = sweep / 2.0
    midpoint = _add(center, _add(_scale(u_axis, radius * math.cos(half)),
                                 _scale(v_axis, radius * math.sin(half))))
    return center, radius, oriented, sweep, u_axis, v_axis, midpoint


def _deviation_ok(points, i, j, arc, tolerance):
    center, radius, normal, sweep, u_axis, v_axis, _midpoint = arc

    # the tolerance translated into an angle, for the order along the arc
    slack = tolerance / radius
    previous = 0.0
    for k in range(i + 1, j):
        offset = _sub(points[k], center)
        off_plane = _dot(offset, normal)
        in_plane = _sub(offset, _scale(normal, off_plane))
        radial = _length(in_plane) - radius
        if math.sqrt(off_plane * off_plane + radial * radial) > tolerance:
            return False

        angle = _angle_on(center, normal, u_axis, v_axis, points[k])
        # a vertex just before the start falls near 2 pi: bring it back to
        # a small negative angle before comparing
        if angle > sweep + slack and angle > math.pi:
            angle -= 2.0 * math.pi
        if angle < previous - slack or angle > sweep + slack:
            return False
        previous = max(previous, angle)
    return True


def fit_arc(points, i, j, tolerance):
    """The midpoint at half sweep of the arc from i to j that holds up the
    tolerance on one of the allowed planes, or None.

    It is the midpoint at half sweep, not the middle vertex, that travels
    on the wire: the vertex sits on the arc only within tolerance, the
    midpoint at half sweep sits on it by construction, and the three points
    define in Revit exactly the plane chosen here."""
    if j - i < 2:
        return None
    for normal in arc_plane_normals(points[i], points[(i + j) // 2], points[j]):
        arc = _arc_in_plane(points, i, j, normal)
        if arc is not None and _deviation_ok(points, i, j, arc, tolerance):
            return arc[6]
    return None


def _arc_fits(points, i, j, tolerance):
    return fit_arc(points, i, j, tolerance) is not None


def _extend(points, i, first, fits, tolerance):
    """The farthest index j >= first for which fits(i, j) holds, or None if
    it does not hold even for `first`.

    Doubling the step and then a binary search: O(k log k) checks for a
    segment k vertices long instead of O(k^2). It assumes the fit is
    monotone, i.e. that if it holds up to j it also holds before: true for
    the real cases (a regular curve), and where it is not the result stays
    correct anyway, just with a few extra segments."""
    last = len(points) - 1
    if first > last or not fits(points, i, first, tolerance):
        return None

    good = first
    bad = None
    step = 1
    while True:
        candidate = min(good + step, last)
        if candidate == good:
            break
        if fits(points, i, candidate, tolerance):
            good = candidate
            step *= 2
        else:
            bad = candidate
            break

    if bad is not None:
        while bad - good > 1:
            middle = (good + bad) // 2
            if fits(points, i, middle, tolerance):
                good = middle
            else:
                bad = middle
    return good


def fit_chain(points, tolerance):
    """Rewrites a chain of points as (lines, arcs).

    Lines are pairs (start, end), arcs are triples (start, end, midpoint at
    half sweep), in the order of Arc.Create(end0, end1, pointOnArc).

    Greedy advancement: from every vertex the segment that reaches the
    farthest is taken. Ties go to the line, which is the simplest element
    and the one Revit has no doubts about."""
    lines = []
    arcs = []
    last = len(points) - 1
    i = 0
    while i < last:
        line_end = _extend(points, i, i + 1, _line_fits, tolerance)
        if line_end is None:
            # only a zero-length segment fails even as a line:
            # prepare_edges has already removed them, but skipping it costs
            # nothing
            i += 1
            continue

        arc_end = _extend(points, i, i + 2, _arc_fits, tolerance)
        if arc_end is not None and arc_end > line_end:
            arcs.append((points[i], points[arc_end],
                         fit_arc(points, i, arc_end, tolerance)))
            i = arc_end
        else:
            lines.append((points[i], points[line_end]))
            i = line_end

    return lines, arcs


def fit_edges(edges, tolerance):
    """(lines, arcs, chains) for all the edges. `tolerance` in meters."""
    lines = []
    arcs = []
    chains = build_chains(edges)
    for chain in chains:
        chain_lines, chain_arcs = fit_chain(chain, tolerance)
        lines.extend(chain_lines)
        arcs.extend(chain_arcs)
    return lines, arcs, len(chains)
