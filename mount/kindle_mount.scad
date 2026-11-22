// Kindle dashboard: door mount for a Kindle Paperwhite 3 hanging in landscape.
// A slot that is closed at the top, bottom and left and open to the right: the Kindle slides in from the right and is
// pulled out to the right. The power button and the USB port are on the open side. Two spring tongues in the back
// press the Kindle lightly against the front lips, so it does not rattle or slide out by itself. The mount is held
// on the door by two tesa Powerstrips Large, whose fields are marked on the back.
//
// Coordinates: X along the Kindle (to the right), Y up, Z away from the door.
// Parts:  "assembly" - preview with a Kindle in it
//         "print"    - standing on its closed left end, prints without supports
//         "test"     - a short section with one spring, to check the fit before printing the whole mount
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

part = "assembly"; // [assembly, print, test]

/* [Kindle] */
kindle_l = 169;    // long edge, horizontal when hanging (Paperwhite 3: 169 x 117 x 9.1 mm)
kindle_h = 117;    // short edge
kindle_t = 9.1;    // thickness

/* [Mount] */
overhang = 20;     // how far the Kindle sticks out on the right, to take hold of it
clr_h    = 0.4;    // play above and below the Kindle
clr_t    = 0.6;    // play in the thickness; the springs take it up
back     = 3.0;    // back plate (on the door)
wall     = 2.0;    // top and bottom wall
end_wall = 2.0;    // left end wall
lip_t    = 2.0;    // thickness of the front lips
lip_w    = 5;      // how far the lips reach over the bezel (about 12 mm wide or more on the Paperwhite 3)
lip_bevel = 1.0;   // bevel of the lips toward the screen
edge     = 1.0;    // chamfer of the outer edges
entry    = 1.5;    // lead-in chamfer at the open end

/* [Springs] */
springs     = true;
spring_l    = 25;   // length of a tongue (along X)
spring_w    = 12;   // width of a tongue
spring_t    = 1.6;  // thickness of a tongue; the back plate is thinned from behind to it
spring_gap  = 0.8;  // slot around a tongue
spring_bump = 1.6;  // how far the bump reaches into the slot; the tongue bends by spring_bump - clr_t
bump_r      = 6;    // radius of the bump; 6 mm keeps its flank printable standing

/* [Powerstrips] */
strip_marks = true; // mark the fields of the strips on the back
strip_w     = 20;   // tesa Powerstrips Large: 20 x 50 mm
strip_l     = 50;
strip_tab   = 12;   // the pull tab sticks out below the mount, the strip is pulled out downward
strip_inset = 10;   // distance of the fields from the left and right end
mark_depth  = 0.4;
mark_line   = 0.8;

/* [Test section] */
test_slot = 35;     // length of the slot of the test section

$fn = 64;
eps = 0.01;

// --- derived ---
slot_l = kindle_l - overhang;
slot_h = kindle_h + 2 * clr_h;
slot_t = kindle_t + clr_t;
H = slot_h + 2 * wall;
D = back + slot_t + lip_t;
bump_len = 2 * sqrt(bump_r * bump_r - (bump_r - spring_bump) * (bump_r - spring_bump));

assert(back - spring_t + 0.65 >= spring_bump - clr_t, "The tongue would hit the door when it bends: thicker back or thinner tongue");
assert(bump_len + 2 <= spring_l, "The bump is longer than the tongue");

// --- parts ---
if (part == "assembly") {
   mount(slot_l, spring_positions(slot_l), strip_marks);
   kindle();
} else if (part == "print") {
   standing(slot_l) mount(slot_l, spring_positions(slot_l), strip_marks);
} else if (part == "test") {
   standing(test_slot) mount(test_slot, [end_wall + test_slot / 2], false);
}

// Spring centers at 30 % and 70 % of the length, clear of the Powerstrip fields near the ends
function spring_positions(sl) = springs ? [(end_wall + sl) * 0.3, (end_wall + sl) * 0.7] : [];

/// Turns the mount so that its closed left end stands on the print bed.
module standing(sl) {
   translate([D, 0, 0]) rotate([0, -90, 0]) children();
}

module mount(sl, spring_xs, marks) {
   L = end_wall + sl;
   difference() {
      chamfer_cube([L, H, D], edge);
      // Slot, open to the right
      translate([end_wall, wall, back]) cube([sl + 1, slot_h, slot_t]);
      // Window: the lips cover lip_w of the bezel and are beveled toward the screen
      hull() {
         translate([end_wall + lip_w, wall + lip_w, back + slot_t - eps])
            cube([sl - lip_w + 1, slot_h - 2 * lip_w, 2 * eps]);
         translate([end_wall + lip_w - lip_bevel, wall + lip_w - lip_bevel, D - eps])
            cube([sl - lip_w + lip_bevel + 1, slot_h - 2 * lip_w + 2 * lip_bevel, 2 * eps]);
      }
      // Lead-in at the open end
      hull() {
         translate([L - entry, wall, back]) cube([eps, slot_h, slot_t]);
         translate([L, wall - entry, back - entry]) cube([1, slot_h + 2 * entry, slot_t + 2 * entry]);
      }
      for (x = spring_xs) translate([x - spring_l / 2, (H - spring_w) / 2, 0]) spring_cut();
      if (marks)
         for (x = [strip_inset, L - strip_inset - strip_w]) translate([x, 0, 0]) strip_mark();
   }
   for (x = spring_xs) translate([x - spring_l / 2, (H - spring_w) / 2, 0]) spring_bump();
}

/// Tongue in local coordinates: root at x = 0, tip at x = spring_l, front face at z = back.
module spring_cut() {
   // Slot on both sides and around the tip
   difference() {
      translate([0, -spring_gap, -1]) cube([spring_l + spring_gap, spring_w + 2 * spring_gap, back + 2]);
      translate([-1, 0, -2]) cube([spring_l + 1, spring_w, back + 4]);
   }
   // Thinned from behind, so it can bend back without touching the door
   translate([0, 0, -1]) cube([spring_l, spring_w, back - spring_t + 1]);
}

/// Rounded bump at the tip of a tongue, so the Kindle slides over it in both directions.
module spring_bump() {
   cx = spring_l - bump_len / 2 - 1;
   intersection() {
      translate([cx, 0, back + spring_bump - bump_r]) rotate([-90, 0, 0]) cylinder(r = bump_r, h = spring_w);
      translate([0, 0, back - eps]) cube([spring_l, spring_w, spring_bump + 1]);
   }
}

/// Outline of a Powerstrip field on the back, open at the bottom where the pull tab comes out.
module strip_mark() {
   len = strip_l - strip_tab;
   difference() {
      translate([0, -1, -1]) cube([strip_w, len + 1, mark_depth + 1]);
      translate([mark_line, -2, -2]) cube([strip_w - 2 * mark_line, len + 2 - mark_line, mark_depth + 3]);
   }
}

module chamfer_cube(s, c) {
   hull() {
      translate([c, 0, c]) cube([s.x - 2 * c, s.y, s.z - 2 * c]);
      translate([0, c, c]) cube([s.x, s.y - 2 * c, s.z - 2 * c]);
      translate([c, c, 0]) cube([s.x - 2 * c, s.y - 2 * c, s.z]);
   }
}

/// The Kindle for the preview, pressed against the lips. The position of the screen is approximate.
module kindle() {
   screen = [122.4, 90.6];   // 6", 1448 x 1072 at 300 ppi, in landscape
   screen_left = 17;          // bezel on the left (the top edge of the Kindle in portrait)
   translate([end_wall, wall + clr_h, back + clr_t]) {
      color("#2b2b2b") linear_extrude(kindle_t)
         offset(r = 6) offset(delta = -6) square([kindle_l, kindle_h]);
      color("#d8d6cf") translate([screen_left, (kindle_h - screen.y) / 2, kindle_t - 0.5])
         cube([screen.x, screen.y, 0.6]);
   }
}
