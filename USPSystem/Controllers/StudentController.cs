using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USPSystem.Data;
using USPSystem.Models;
using USPSystem.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using USPSystem.Filters;

namespace USPSystem.Controllers;

[Authorize(Roles = "Student")]
public class StudentController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly new UserManager<ApplicationUser> _userManager;
    private readonly IStudentGradeService _gradeService;
    private readonly ILogger<StudentController> _logger;
    private readonly IConfiguration _configuration;

    public StudentController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IStudentGradeService gradeService,
        StudentHoldService studentHoldService,
        PageHoldService pageHoldService,
        ILogger<StudentController> logger,
        IConfiguration configuration)
        : base(studentHoldService, pageHoldService, userManager)
    {
        _context = context;
        _userManager = userManager;
        _gradeService = gradeService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        var enrollments = await _context.StudentEnrollments
            .Include(e => e.Course)
            .Where(e => e.StudentId == user.Id)
            .OrderBy(e => e.Year)
            .ThenBy(e => e.Semester)
            .ToListAsync();

        return View(enrollments);
    }

    public IActionResult Enroll()
    {
        return RedirectToAction(nameof(AvailableCourses));
    }

    public async Task<IActionResult> Requirements()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // Get completed courses from external grade system
        var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);
        ViewBag.CompletedCourses = completedCourseIds;

        var requirements = await _context.ProgramRequirements
            .Include(r => r.SubjectArea)
            .Include(r => r.RequiredCourses)
            .Where(r => r.Year <= user.AdmissionYear && r.IsActive)
            .ToListAsync();

        // Get core requirements
        var coreRequirements = requirements.Where(r =>
            r.Type == RequirementType.MajorCore ||
            r.Type == RequirementType.MinorCore ||
            r.Type == RequirementType.ProgressionRequirement
        ).ToList();

        // Get major requirements
        var majorRequirements = requirements.Where(r =>
            (r.Type == RequirementType.MajorCore || r.Type == RequirementType.MajorElective) &&
            r.SubjectArea.Code == user.MajorI
        ).ToList();

        if (user.MajorType == MajorType.DoubleMajor)
        {
            // Add second major requirements
            majorRequirements.AddRange(requirements.Where(r =>
                (r.Type == RequirementType.MajorCore || r.Type == RequirementType.MajorElective) &&
                r.SubjectArea.Code == user.MajorII
            ));
        }
        else if (user.MajorType == MajorType.MajorMinor)
        {
            // Add minor requirements
            majorRequirements.AddRange(requirements.Where(r =>
                (r.Type == RequirementType.MinorCore || r.Type == RequirementType.MinorElective) &&
                r.SubjectArea.Code == user.MinorI
            ));
        }

        ViewBag.MajorType = user.MajorType;
        ViewBag.MajorI = user.MajorI;
        ViewBag.MajorII = user.MajorII;
        ViewBag.MinorI = user.MinorI;
        ViewBag.AdmissionYear = user.AdmissionYear;

        return View(majorRequirements);
    }

    private bool IsCourseCompleted(int courseId)
    {
        return ViewBag.CompletedCourses?.Contains(courseId) ?? false;
    }

    public async Task<IActionResult> AvailableCourses()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // Get completed courses from external grade system
        var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);

        ViewBag.CompletedCourses = completedCourseIds;

        var enrolledCourseIds = await _context.StudentEnrollments
            .Where(e => e.StudentId == user.Id)
            .Select(e => e.CourseId)
            .ToListAsync();

        var availableCourses = await _context.Courses
            .Include(c => c.Prerequisites)
            .Where(c => !enrolledCourseIds.Contains(c.Id))
            .Where(p => !completedCourseIds.Contains(p.Id))
            .OrderBy(c => c.Code)
            .ToListAsync();

        return View(availableCourses);
    }

    public async Task<IActionResult> EnrollDetails(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        var course = await _context.Courses
            .Include(c => c.Prerequisites)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (course == null)
            return NotFound();

        // Get completed courses from external grade system
        var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);

        ViewBag.CompletedCourses = completedCourseIds;

        return View("Enroll", course);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enroll(int courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        var course = await _context.Courses
            .Include(c => c.Prerequisites)
            .FirstOrDefaultAsync(c => c.Id == courseId);

        if (course == null)
            return NotFound();

        // Check if already enrolled
        var existingEnrollment = await _context.StudentEnrollments
            .AnyAsync(e => e.StudentId == user.Id && e.CourseId == courseId);

        if (existingEnrollment)
        {
            ModelState.AddModelError("", "You are already enrolled in this course.");
            return View(course);
        }

        // Check prerequisites using external grade system
        if (course.Prerequisites.Any())
        {
            var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);
            var prerequisiteIds = course.Prerequisites.Select(p => p.Id).ToList();
            var completedPrerequisites = prerequisiteIds.Count(id => completedCourseIds.Contains(id));

            if (completedPrerequisites < course.Prerequisites.Count)
            {
                ModelState.AddModelError("", "You have not completed all prerequisites for this course.");
                return View(course);
            }
        }

        var enrollment = new StudentEnrollment
        {
            StudentId = user.Id,
            CourseId = courseId,
            Year = DateTime.Now.Year,
            Semester = GetCurrentSemester()
        };

        _context.StudentEnrollments.Add(enrollment);
        await _context.SaveChangesAsync();

        // Trigger finance update
        await UpdateStudentFinance(user.StudentId);

        return RedirectToAction(nameof(Index));
    }

    private async Task UpdateStudentFinance(string studentId)
    {
        try
        {
            // Create a new DbContext for the finance database
            var optionsBuilder = new DbContextOptionsBuilder<USPFinance.Data.AppDbContext>();
            optionsBuilder.UseSqlServer(_configuration.GetConnectionString("FinanceConnection"));
            
            using var financeContext = new USPFinance.Data.AppDbContext(optionsBuilder.Options);
            
            // Get the user from AspNetUsers using the StudentId (S-number)
            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.StudentId == studentId);
            if (user == null)
            {
                _logger.LogError("User not found for StudentId {StudentId}", studentId);
                return;
            }

            _logger.LogInformation("Found user - Id: {Id}, StudentId: {StudentId}", user.Id, user.StudentId);

            // Get all active enrollments for the student with course details
            var enrollments = await _context.StudentEnrollments
                .Include(e => e.Course)
                .Where(e => e.StudentId == user.Id && e.IsActive)
                .ToListAsync();

            _logger.LogInformation("Found {Count} active enrollments for user", enrollments.Count);

            // Calculate total fees from active enrollments
            decimal totalFees = 0;
            foreach (var enrollment in enrollments)
            {
                if (enrollment.Course?.Fees != null)
                {
                    totalFees += enrollment.Course.Fees.Value;
                    _logger.LogInformation("Adding fees for course {CourseCode}: {Fees}", 
                        enrollment.Course.Code, enrollment.Course.Fees.Value);
                }
            }

            _logger.LogInformation("Total fees calculated: {TotalFees}", totalFees);

            // Get existing finance record using StudentId
            var studentFinance = await financeContext.StudentFinances
                .FirstOrDefaultAsync(sf => sf.StudentID == user.StudentId);

            if (studentFinance == null)
            {
                // Create new finance record
                studentFinance = new USPFinance.Models.StudentFinance
                {
                    StudentID = user.StudentId,  // Use StudentId from AspNetUsers
                    TotalFees = totalFees,
                    AmountPaid = 0,
                    LastUpdated = DateTime.UtcNow,
                    IsOnHold = false,
                    HoldReason = string.Empty,
                    HoldPlacedBy = string.Empty
                };
                financeContext.StudentFinances.Add(studentFinance);
                _logger.LogInformation("Created new finance record for StudentId: {StudentId}", user.StudentId);
            }
            else
            {
                // Update existing record
                studentFinance.TotalFees = totalFees;
                studentFinance.LastUpdated = DateTime.UtcNow;
                _logger.LogInformation("Updated existing finance record for StudentId: {StudentId}", user.StudentId);
            }

            await financeContext.SaveChangesAsync();
            _logger.LogInformation("Successfully saved finance record - StudentId: {StudentId}, TotalFees: {TotalFees}", 
                user.StudentId, totalFees);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating student finance for {StudentId}", studentId);
        }
    }

    public async Task<IActionResult> CourseDetails(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        var course = await _context.Courses
            .Include(c => c.Prerequisites)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (course == null)
            return NotFound();

        // Get completed courses from external grade system
        var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);

        ViewBag.CompletedCourses = completedCourseIds;

        return View(course);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unenroll(int enrollmentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        var enrollment = await _context.StudentEnrollments
            .FirstOrDefaultAsync(e => e.Id == enrollmentId && e.StudentId == user.Id);

        if (enrollment == null)
            return NotFound();

        _context.StudentEnrollments.Remove(enrollment);
        await _context.SaveChangesAsync();

        // Update finance record after unenrolling
        await UpdateStudentFinance(user.StudentId);

        TempData["Success"] = "Successfully unenrolled from the course.";
        return RedirectToAction(nameof(Index));
    }

    private static Semester GetCurrentSemester()
    {
        var month = DateTime.Now.Month;
        return month switch
        {
            >= 1 and <= 6 => Semester.Semester1,
            >= 7 and <= 11 => Semester.Semester2,
            _ => Semester.Semester1
        };
    }

    [Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // Load the complete user profile with related data
        var profile = await _context.Users
            .Include(u => u.Enrollments)
                .ThenInclude(e => e.Course)
            .FirstOrDefaultAsync(u => u.Id == user.Id);

        if (profile == null)
            return NotFound();

        return View(profile);
    }

    [Authorize]
    public async Task<IActionResult> Grades()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // Get grades from external grade system
        var grades = await _gradeService.GetGradesAsync(user.StudentId);
        if (grades == null)
            grades = new List<GradeViewModel>();

        // Get course information for each grade
        foreach (var grade in grades)
        {
            var course = await _context.Courses
                .FirstOrDefaultAsync(c => c.Code == grade.CourseCode);
            if (course != null)
            {
                grade.CourseName = course.Name;
            }
            // Check if recheck has been applied
            try
            {
                grade.HasAppliedForRecheck = await _gradeService.HasAppliedForRecheckAsync(user.StudentId, grade.CourseCode, grade.Year, grade.Semester);
                Console.WriteLine($"Grade {grade.CourseCode}: HasAppliedForRecheck = {grade.HasAppliedForRecheck}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking recheck status for {grade.CourseCode}: {ex.Message}");
                grade.HasAppliedForRecheck = false;
            }
        }

        return View(grades);
    }

    [Authorize]
    public async Task<IActionResult> ApplyForRecheck(int gradeId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // Get all grades for the student
        var grades = await _gradeService.GetGradesAsync(user.StudentId);

        // Find the specific grade by ID
        var grade = grades?.FirstOrDefault(g => g.Id == gradeId);

        if (grade == null)
            return NotFound();

        // Check if already applied
        if (await _gradeService.HasAppliedForRecheckAsync(user.StudentId, grade.CourseCode, grade.Year, grade.Semester))
        {
            TempData["ErrorMessage"] = "You have already applied for a recheck for this course.";
            return RedirectToAction(nameof(Grades));
        }

        var model = new RecheckApplicationModel
        {
            GradeId = grade.Id,
            CourseCode = grade.CourseCode,
            CourseName = grade.CourseName,
            Year = grade.Year,
            Semester = grade.Semester,
            CurrentGrade = grade.Grade,
            Email = user.Email ?? "" // Pre-fill with user's email if available
        };

        return View("RecheckApplication", model);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [GradeRecheckFilter]
    public async Task<IActionResult> SubmitRecheckApplication(RecheckApplicationModel model)
    {
        if (!ModelState.IsValid)
        {
            // Get the grade information to repopulate the form
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound();

            var grades = await _gradeService.GetGradesAsync(user.StudentId);
            var grade = grades?.FirstOrDefault(g => g.Id == model.GradeId);

            if (grade != null)
            {
                // Repopulate the form data
                model.CourseCode = grade.CourseCode;
                model.CourseName = grade.CourseName;
                model.CurrentGrade = grade.Grade;

                // Keep the submitted year and semester if present
                if (model.Year <= 0)
                {
                    model.Year = grade.Year;
                }

                if (model.Semester <= 0)
                {
                    model.Semester = grade.Semester;
                }
            }

            return View("RecheckApplication", model);
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return NotFound();

        try
        {
            // Submit to grade system API
            var result = await _gradeService.ApplyForRecheckAsync(
                currentUser.StudentId,
                model.CourseCode,
                model.Year,
                model.Semester,
                model.Reason,
                model.AdditionalComments,
                model.Email,
                model.CourseName,
                model.CurrentGrade,
                model.PaymentReceiptNumber);

            if (result)
            {
                TempData["SuccessMessage"] = "Your recheck application has been submitted successfully. You will receive updates at " + model.Email;
                return RedirectToAction(nameof(Grades));
            }

            TempData["ErrorMessage"] = "Failed to submit recheck application. Please try again later.";
            return RedirectToAction(nameof(Grades));
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = "An error occurred while processing your request: " + ex.Message;
            return View("RecheckApplication", model);
        }
    }

    [Authorize]
    public async Task<IActionResult> ApplyForGraduation()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // Check if the student has completed enough courses to graduate
        var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);

        // For now, just display a simple view
        ViewBag.CompletedCoursesCount = completedCourseIds.Count;
        ViewBag.CanGraduate = completedCourseIds.Count >= 24; // Example threshold
        ViewBag.StudentName = $"{user.FirstName} {user.LastName}";
        ViewBag.StudentId = user.StudentId;
        ViewBag.Program = $"{user.MajorI} {(user.MajorType == MajorType.DoubleMajor ? "/ " + user.MajorII : "")}";
        ViewBag.MajorI = user.MajorI;
        ViewBag.MajorII = user.MajorII;
        ViewBag.MinorI = user.MinorI;

        // Calculate expected graduation date (example: next semester end)
        ViewBag.ExpectedGraduationDate = DateTime.Now.Month < 6
            ? new DateTime(DateTime.Now.Year, 7, 15)
            : new DateTime(DateTime.Now.Year + 1, 1, 15);

        // Check if student already has a pending graduation application
        var existingApplication = await _context.GraduationApplications
            .FirstOrDefaultAsync(g => g.StudentId == user.UserName && g.Status == ApplicationStatus.Pending);

        if (existingApplication != null)
        {
            ViewBag.HasPendingApplication = true;
            ViewBag.ApplicationDate = existingApplication.ApplicationDate;
            ViewBag.ApplicationStatus = existingApplication.Status;
            ViewBag.GraduationCeremony = existingApplication.GraduationCeremony;
        }

        return View();
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    [EmailNotificationFilter]
    public async Task<IActionResult> SubmitGraduationApplication(GraduationApplicationViewModel model)
    {
        if (!ModelState.IsValid)
        {
            // Re-populate the ViewBag data for the view
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound();

            var completedCourseIds = await _gradeService.GetCompletedCourseIdsAsync(user.StudentId);

            ViewBag.CompletedCoursesCount = completedCourseIds.Count;
            ViewBag.CanGraduate = completedCourseIds.Count >= 24;
            ViewBag.StudentName = $"{user.FirstName} {user.LastName}";
            ViewBag.StudentId = user.StudentId;
            ViewBag.Program = $"{user.MajorI} {(user.MajorType == MajorType.DoubleMajor ? "/ " + user.MajorII : "")}";
            ViewBag.MajorI = user.MajorI;
            ViewBag.MajorII = user.MajorII;
            ViewBag.MinorI = user.MinorI;

            // Return to the form with validation errors
            return View("ApplyForGraduation", model);
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return NotFound();

        // Check for existing application - for display purposes only
        var existingApplication = await _context.GraduationApplications
            .FirstOrDefaultAsync(g => g.StudentId == currentUser.UserName && g.Status == ApplicationStatus.Pending);

        // Create new graduation application
        var application = new GraduationApplication
        {
            StudentId = currentUser.UserName, // This should be Id to match the FK constraint, not StudentId
            FirstName = currentUser.FirstName,
            Surname = currentUser.LastName,
            PostalAddress = model.PostalAddress,
            DateOfBirth = model.DateOfBirth,
            Telephone = model.Telephone,
            Email = model.Email,
            Programme = currentUser.MajorI + (currentUser.MajorType == MajorType.DoubleMajor ? " / " + currentUser.MajorII : ""),
            MajorI = currentUser.MajorI,
            MajorII = currentUser.MajorII,
            Minor = currentUser.MinorI,
            GraduationCeremony = model.GraduationCeremony,
            WillAttend = model.WillAttend,
            DeclarationConfirmed = model.ConfirmDeclaration,
            ApplicationDate = DateTime.Now,
            Status = ApplicationStatus.Pending
        };

        _context.GraduationApplications.Add(application);
        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = "Your graduation application has been submitted successfully. You will be notified once it has been processed.";
        return RedirectToAction(nameof(Index));
    }
    
    // ✅ TRANSCRIPT FEATURE – START

    [Authorize]
    [HttpGet]
    public IActionResult Transcript()
    {
        return View();
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EmailNotificationFilter]
    public async Task<IActionResult> GenerateTranscript()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        string studentId = user.StudentId;

        var client = new HttpClient();
        var authBytes = Encoding.ASCII.GetBytes("admin:password123"); // ← use this
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var response = await client.GetAsync($"http://localhost:5241/transcript/{studentId}");

        if (response.IsSuccessStatusCode)
        {
            var fileBytes = await response.Content.ReadAsByteArrayAsync();
            return File(fileBytes, "application/pdf", $"Transcript_{studentId}.pdf");
        }
        else
        {
            // ✅ Read the actual response content from Flask
            string errorDetails = await response.Content.ReadAsStringAsync();

            // ✅ Optional: Log it or print to console
            Console.WriteLine($"Transcript API Error: {errorDetails}");

            // ✅ Show error on page
            TempData["Error"] = $"Unable to retrieve transcript. API returned: {errorDetails}";
            return RedirectToAction("Transcript");
        }
    }

    // ✅ TRANSCRIPT FEATURE – END
}

