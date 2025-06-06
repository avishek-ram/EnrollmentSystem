using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USPFinance.Data;
using USPFinance.Models;
using System.Collections.Generic;

namespace USPFinance.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class StudentFinanceController : ControllerBase
    {
        private readonly AppDbContext _context;

        public StudentFinanceController(AppDbContext context)
        {
            _context = context;
        }

        // GET: api/StudentFinance
        [HttpGet]
        public async Task<ActionResult<IEnumerable<StudentFinance>>> GetStudentFinances()
        {
            return await _context.StudentFinances.ToListAsync();
        }

        // GET: api/StudentFinance/5
        [HttpGet("{id}")]
        public async Task<ActionResult<StudentFinance>> GetStudentFinance(string id)
        {
            var studentFinance = await _context.StudentFinances
                .FirstOrDefaultAsync(s => s.StudentID == id);

            if (studentFinance == null)
            {
                return NotFound();
            }

            return studentFinance;
        }

        // GET: api/StudentFinance/{studentId}/details
        [HttpGet("{studentId}/details")]
        public async Task<ActionResult<StudentFinanceDetails>> GetStudentFinanceDetails(string studentId)
        {
            var studentFinance = await _context.StudentFinances
                .FirstOrDefaultAsync(s => s.StudentID == studentId);

            if (studentFinance == null)
            {
                return NotFound();
            }

            // Get related transactions for this student
            var transactions = await _context.Transactions
                .Where(t => t.Description.Contains(studentId) && t.TransactionType == "Income")
                .ToListAsync();

            // Create a detailed response
            var details = new StudentFinanceDetails
            {
                StudentID = studentFinance.StudentID,
                TotalFees = studentFinance.TotalFees,
                AmountPaid = studentFinance.AmountPaid,
                OutstandingBalance = studentFinance.TotalFees - studentFinance.AmountPaid,
                LastUpdated = studentFinance.LastUpdated,
                PaymentHistory = transactions.Select(t => new PaymentRecord
                {
                    Date = t.Date,
                    Amount = t.Amount,
                    Description = t.Description,
                    Notes = t.Notes
                }).ToList()
            };

            return details;
        }

        // PUT: api/StudentFinance/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutStudentFinance(string id, StudentFinance studentFinance)
        {
            if (id != studentFinance.StudentID)
            {
                return BadRequest();
            }

            // Check if balance is zero and remove hold if it is
            if (studentFinance.TotalFees - studentFinance.AmountPaid <= 0 && studentFinance.IsOnHold)
            {
                studentFinance.IsOnHold = false;
                studentFinance.HoldEndDate = DateTime.UtcNow;
                studentFinance.HoldReason = string.Empty;
                studentFinance.HoldPlacedBy = string.Empty;
            }

            _context.Entry(studentFinance).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!StudentFinanceExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }

        // POST: api/StudentFinance
        [HttpPost]
        public async Task<ActionResult<StudentFinance>> PostStudentFinance(StudentFinance studentFinance)
        {
            _context.StudentFinances.Add(studentFinance);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (StudentFinanceExists(studentFinance.StudentID))
                {
                    return Conflict();
                }
                else
                {
                    throw;
                }
            }

            return CreatedAtAction("GetStudentFinance", new { id = studentFinance.StudentID }, studentFinance);
        }

        // DELETE: api/StudentFinance/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteStudentFinance(string id)
        {
            var studentFinance = await _context.StudentFinances
                .FirstOrDefaultAsync(s => s.StudentID == id);
            if (studentFinance == null)
            {
                return NotFound();
            }

            _context.StudentFinances.Remove(studentFinance);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // POST: api/StudentFinance/{studentId}/process-payment
        [HttpPost("{studentId}/process-payment")]
        public async Task<ActionResult<StudentFinance>> ProcessPayment(string studentId, [FromBody] PaymentRequest request)
        {
            var studentFinance = await _context.StudentFinances
                .FirstOrDefaultAsync(s => s.StudentID == studentId);

            if (studentFinance == null)
            {
                return NotFound("Student not found");
            }

            // Validate payment amount
            if (request.Amount <= 0)
            {
                return BadRequest("Payment amount must be greater than zero");
            }

            var outstandingBalance = studentFinance.TotalFees - studentFinance.AmountPaid;
            if (request.Amount > outstandingBalance)
            {
                return BadRequest($"Payment amount cannot exceed outstanding balance of {outstandingBalance:C}");
            }

            // Create transaction record
            var transaction = new Transaction
            {
                Description = $"Payment for student {studentId}",
                Amount = request.Amount,
                Date = DateTime.UtcNow,
                TransactionType = "Income",
                Category = "Student Payment",
                Notes = request.Notes
            };
            _context.Transactions.Add(transaction);

            // Update student finance record
            studentFinance.AmountPaid += request.Amount;
            studentFinance.LastUpdated = DateTime.UtcNow;

            // Check if balance is now zero and remove hold if it is
            var newBalance = studentFinance.TotalFees - studentFinance.AmountPaid;
            if (newBalance <= 0 && studentFinance.IsOnHold)
            {
                studentFinance.IsOnHold = false;
                studentFinance.HoldEndDate = DateTime.UtcNow;
                studentFinance.HoldReason = string.Empty;
                studentFinance.HoldPlacedBy = string.Empty;
            }

            try
            {
                await _context.SaveChangesAsync();
                return Ok(new
                {
                    studentFinance,
                    message = newBalance <= 0 && studentFinance.IsOnHold 
                        ? "Payment processed successfully. Hold has been removed." 
                        : "Payment processed successfully."
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!StudentFinanceExists(studentId))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
        }

        private bool StudentFinanceExists(string id)
        {
            return _context.StudentFinances.Any(e => e.StudentID == id);
        }
    }

    public class PaymentRequest
    {
        public decimal Amount { get; set; }
        public string Notes { get; set; }
    }
} 